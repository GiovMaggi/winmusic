using WinMusic.Audio.Platform;
using WinMusic.Audio.Wasapi;
using WinMusic.Core.Dsp;

namespace WinMusic.Audio.Engine;

public enum SourceKind { App, Mic, External }

public sealed record SourceStatus(string Name, SourceKind Kind, int Pid, bool Playing, double BufferedMs, double CorrectionPpm, long Underruns, string? Error);

/// <summary>
/// The WinMusic mixer:  [app captures] + [mic]  →  drift-compensated  →  mix (32-bit float)
///                      →  transparent limiter  →  CABLE Input  →  "CABLE Output" virtual mic.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private sealed class Source
    {
        public required string Key;
        public required string Name;
        public required SourceKind Kind;
        public required DriftSource Drift;
        public CaptureStream? Capture;
        public int Pid;
        public string? DeviceId;
        public volatile bool Failed;
        public string? Error;
        public readonly GainRamp Gain = new(1f);
    }

    private readonly object _lock = new();
    private volatile Source[] _sources = Array.Empty<Source>();
    private RenderStream? _render;
    private volatile bool _renderFailed;
    private Limiter? _limiter;
    private float[] _tmp = new float[48000 * 2];
    private int _outRate;
    private Thread? _watcher;
    private volatile bool _stop;

    // settings (guarded by _lock)
    private List<string> _apps = new();
    private readonly List<int> _appPids = new();
    private string? _micDeviceId;          // null = automatic, "" = no mic
    private volatile float _musicGain = 1f, _micGain = 1f;
    private volatile bool _micMuted;
    private string? _outputDeviceId;

    // meters (render thread writes, UI reads & resets)
    private float _peakMusic, _peakMic, _peakOut, _minLimiterGain = 1f;

    public event Action<string>? Log;
    public bool IsRunning => _render != null && !_stop;
    public WaveFormatInfo? OutputFormat => _render?.Format;
    public string? OutputDeviceName { get; private set; }
    public double OutputLatencyMs => _render?.LatencyMs ?? 0;

    // ---------- control ----------

    /// <param name="outputDeviceId">Render device to send the mix to; null = VB-CABLE "CABLE Input".</param>
    public void Start(string? outputDeviceId = null)
    {
        if (IsRunning) return;
        _stop = false;
        var dev = outputDeviceId != null
            ? Devices.List(DeviceFlow.Playback).FirstOrDefault(d => d.Id == outputDeviceId)
            : Devices.FindCableInput();
        if (dev == null) throw new InvalidOperationException("VB-CABLE (\"CABLE Input\") was not found. Install it first.");
        _outputDeviceId = dev.Id;
        OutputDeviceName = dev.Name;
        StartRender();
        Reconcile();
        _watcher = new Thread(WatchLoop) { IsBackground = true, Name = "WinMusic watcher" };
        _watcher.SetApartmentState(ApartmentState.MTA);
        _watcher.Start();
    }

    public void Stop()
    {
        _stop = true;
        _watcher?.Join(3000);
        _watcher = null;
        _render?.Dispose();
        _render = null;
        lock (_reconcileLock) // wait for any in-flight reconcile so no capture is left running
        {
            var old = _sources;
            _sources = Array.Empty<Source>();
            foreach (var s in old) s.Capture?.Dispose();
        }
    }

    public void Dispose() => Stop();

    public void SetApps(IEnumerable<string> exeNames)
    {
        lock (_lock) _apps = exeNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (IsRunning) ThreadPool.QueueUserWorkItem(_ => SafeReconcile());
    }

    /// <summary>Capture a specific process id (used by the self test).</summary>
    public void AddAppPid(int pid)
    {
        lock (_lock) _appPids.Add(pid);
        if (IsRunning) SafeReconcile();
    }

    /// <summary>null = automatic (default mic, never the cable), "" = no microphone.</summary>
    public void SetMicDevice(string? deviceId)
    {
        lock (_lock) _micDeviceId = deviceId;
        if (IsRunning) ThreadPool.QueueUserWorkItem(_ => SafeReconcile());
    }

    public void SetMusicGainDb(double db) => _musicGain = GainRamp.DbToGain(db);
    public void SetMicGainDb(double db) => _micGain = GainRamp.DbToGain(db);
    public void SetMicMuted(bool muted) => _micMuted = muted;

    /// <summary>Adds a source fed by the caller (self test signal generators etc.).</summary>
    public DriftSource AddExternalSource(string name, int sampleRate, double latencyMs = 40)
    {
        if (!IsRunning) throw new InvalidOperationException("Start the engine first");
        var src = new Source { Key = "ext:" + name, Name = name, Kind = SourceKind.External, Drift = new DriftSource(sampleRate, _outRate, latencyMs) };
        lock (_lock) _sources = _sources.Append(src).ToArray();
        return src.Drift;
    }

    // ---------- meters ----------

    public (float music, float mic, float output, float limiterGain) ReadAndResetMeters()
    {
        var r = (_peakMusic, _peakMic, _peakOut, _minLimiterGain);
        _peakMusic = 0; _peakMic = 0; _peakOut = 0; _minLimiterGain = 1f;
        return r;
    }

    public List<SourceStatus> GetStatus() => _sources.Select(s => new SourceStatus(
        s.Name, s.Kind, s.Pid, s.Drift.IsPlaying, s.Drift.BufferedMs, s.Drift.CorrectionPpm, s.Drift.Underruns, s.Error)).ToList();

    // ---------- internals ----------

    private void StartRender()
    {
        var render = new RenderStream(_outputDeviceId!, Fill);
        render.Failed += e => { Log?.Invoke($"Output stopped: {e.Message}"); _renderFailed = true; };
        // The limiter and the output rate must be known before the first callback, so peek the mix format first.
        var mix = Devices.GetMixFormat(_outputDeviceId!);
        bool rateChanged = _outRate != 0 && _outRate != mix.SampleRate;
        _outRate = mix.SampleRate;
        _limiter = new Limiter(_outRate);
        _renderFailed = false;
        if (rateChanged)
        {
            // every source was resampling to the old rate: rebuild them
            var old = _sources;
            _sources = Array.Empty<Source>();
            foreach (var s in old) s.Capture?.Dispose();
        }
        render.Start();
        _render = render;
        Log?.Invoke($"Output: {OutputDeviceName} @ {render.Format} (buffer {render.BufferFrames} frames = {render.BufferFrames * 1000.0 / _outRate:F1} ms)");
    }

    private void Fill(Span<float> output, int frames, double now, int queued)
    {
        output.Clear();
        if (_tmp.Length < frames * 2) _tmp = new float[frames * 2];
        var tmp = _tmp.AsSpan(0, frames * 2);
        float musicGain = _musicGain, micGain = _micMuted ? 0f : _micGain;
        float pMusic = 0, pMic = 0;

        foreach (var s in _sources)
        {
            s.Drift.Read(tmp, frames, now, queued);
            float peak = 0;
            for (int i = 0; i < tmp.Length; i++) { float a = Math.Abs(tmp[i]); if (a > peak) peak = a; }
            float g = s.Kind == SourceKind.Mic ? micGain : musicGain;
            s.Gain.Apply(tmp, frames, g);
            if (s.Kind == SourceKind.Mic) pMic = Math.Max(pMic, peak * g); else pMusic = Math.Max(pMusic, peak * g);
            for (int i = 0; i < tmp.Length; i++) output[i] += tmp[i];
        }

        _limiter!.Process(output, frames);
        float pOut = 0;
        for (int i = 0; i < frames * 2; i++) { float a = Math.Abs(output[i]); if (a > pOut) pOut = a; }

        if (pMusic > _peakMusic) _peakMusic = pMusic;
        if (pMic > _peakMic) _peakMic = pMic;
        if (pOut > _peakOut) _peakOut = pOut;
        if (_limiter.LastMinGain < _minLimiterGain) _minLimiterGain = _limiter.LastMinGain;
    }

    private void WatchLoop()
    {
        while (!_stop)
        {
            for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
            if (_stop) break;
            if (_renderFailed)
            {
                try
                {
                    _render?.Dispose();
                    var dev = Devices.FindCableInput();
                    if (dev != null) { _outputDeviceId = dev.Id; StartRender(); Log?.Invoke("Output restarted."); }
                }
                catch (Exception e) { Log?.Invoke($"Waiting for output device: {e.Message}"); }
            }
            SafeReconcile();
        }
    }

    private void SafeReconcile()
    {
        try { Reconcile(); }
        catch (Exception e) { Log?.Invoke($"Error: {e.Message}"); }
    }

    private readonly object _reconcileLock = new();
    private readonly Dictionary<string, DateTime> _retryAfter = new();

    /// <summary>Makes the running captures match the settings; re-attaches when apps restart.</summary>
    private void Reconcile()
    {
        lock (_reconcileLock)
        {
            if (_stop || _render == null) return;
            List<string> apps; List<int> pids; string? micId;
            lock (_lock) { apps = _apps.ToList(); pids = _appPids.ToList(); micId = _micDeviceId; }

            // desired set: key -> (name, kind, pid, deviceId)
            var desired = new Dictionary<string, (string name, SourceKind kind, int pid, string? dev)>();
            foreach (var exe in apps)
            {
                int? pid = Apps.FindRootPid(exe);
                if (pid != null) desired["app:" + exe.ToLowerInvariant()] = (Path.GetFileNameWithoutExtension(exe), SourceKind.App, pid.Value, null);
            }
            foreach (var pid in pids) desired["pid:" + pid] = ($"pid {pid}", SourceKind.App, pid, null);

            if (micId != "")
            {
                AudioDevice? mic = null;
                var recs = Devices.List(DeviceFlow.Recording).Where(d => !d.IsVirtualCable).ToList();
                if (micId != null) mic = recs.FirstOrDefault(d => d.Id == micId);
                if (mic == null)
                {
                    var def = Devices.DefaultId(DeviceFlow.Recording, communications: true);
                    mic = recs.FirstOrDefault(d => d.Id == def) ?? recs.FirstOrDefault(d => d.Id == Devices.DefaultId(DeviceFlow.Recording)) ?? recs.FirstOrDefault();
                }
                if (mic != null) desired["mic"] = (mic.Name, SourceKind.Mic, 0, mic.Id);
            }

            var current = _sources;
            var keep = new List<Source>();
            var remove = new List<Source>();
            foreach (var s in current)
            {
                if (s.Kind == SourceKind.External) { keep.Add(s); continue; }
                if (desired.TryGetValue(s.Key, out var d) && !s.Failed && s.Pid == d.pid && s.DeviceId == d.dev) { keep.Add(s); desired.Remove(s.Key); }
                else remove.Add(s);
            }
            foreach (var (key, d) in desired)
            {
                if (_retryAfter.TryGetValue(key, out var after) && DateTime.UtcNow < after) continue;
                var s = CreateSource(key, d.name, d.kind, d.pid, d.dev);
                if (s != null) { keep.Add(s); _retryAfter.Remove(key); }
                else _retryAfter[key] = DateTime.UtcNow.AddSeconds(10);
            }
            if (remove.Count > 0 || keep.Count != current.Length || keep.Except(current).Any())
                _sources = keep.ToArray();
            foreach (var s in remove)
            {
                s.Capture?.Dispose();
                Log?.Invoke($"Stopped {s.Name}");
            }
        }
    }

    private Source? CreateSource(string key, string name, SourceKind kind, int pid, string? deviceId)
    {
        Source? src = null;
        try
        {
            CaptureStream cap;
            DriftSource? drift = null;
            CaptureStream.DataHandler handler = (data, ch, t, silent) =>
            {
                var d = drift;
                if (d == null) return;
                if (silent) d.WriteSilence(data.Length / ch, t); else d.Write(data, ch, t);
            };
            if (kind == SourceKind.Mic)
            {
                cap = CaptureStream.ForDevice(deviceId!, handler);
                var mf = Devices.GetMixFormat(deviceId!);
                drift = new DriftSource(mf.SampleRate, _outRate, targetLatencyMs: 30);
            }
            else
            {
                cap = CaptureStream.ForProcess(pid, _outRate, 2, handler);
                drift = new DriftSource(_outRate, _outRate, targetLatencyMs: 40);
            }
            src = new Source { Key = key, Name = name, Kind = kind, Drift = drift, Capture = cap, Pid = pid, DeviceId = deviceId };
            var s = src;
            cap.Failed += e => { s.Failed = true; s.Error = e.Message; Log?.Invoke($"{s.Name}: {e.Message}"); };
            cap.Start();
            Log?.Invoke(kind == SourceKind.Mic ? $"Microphone: {name} ({cap.Format})" : $"Capturing {name} (pid {pid}) at {cap.Format}");
            return src;
        }
        catch (Exception e)
        {
            Log?.Invoke($"Could not capture {name}: {e.Message}");
            src?.Capture?.Dispose();
            return null;
        }
    }
}
