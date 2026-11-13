using System.Diagnostics;
using System.Runtime.InteropServices;
using WinMusic.Audio.Interop;

namespace WinMusic.Audio.Wasapi;

public static class AudioClock
{
    /// <summary>Seconds on the QPC clock (same clock WASAPI uses for capture timestamps).</summary>
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

/// <summary>Base for a WASAPI stream that runs on its own high priority MTA thread.</summary>
public abstract class StreamBase : IDisposable
{
    private Thread? _thread;
    private volatile bool _stop;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _initError;

    public WaveFormatInfo? Format { get; protected set; }
    public event Action<Exception>? Failed;
    public bool IsRunning => _thread != null && !_stop;
    protected bool StopRequested => _stop;
    protected abstract string Name { get; }

    public void Start(int timeoutMs = 8000)
    {
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = Name, Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_ready.Wait(timeoutMs)) { _stop = true; throw new TimeoutException($"{Name}: timed out starting"); }
        if (_initError != null) { _thread.Join(2000); throw _initError; }
    }

    private void ThreadProc()
    {
        uint taskIndex = 0;
        IntPtr mm = IntPtr.Zero;
        try { mm = Native.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex); } catch { }
        try
        {
            Run(() => _ready.Set());
        }
        catch (Exception e)
        {
            if (!_ready.IsSet) { _initError = e; _ready.Set(); }
            else if (!_stop) Failed?.Invoke(e);
        }
        finally
        {
            if (mm != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mm);
        }
    }

    /// <summary>Open the device, call ready(), then loop until StopRequested.</summary>
    protected abstract void Run(Action ready);

    public virtual void Dispose()
    {
        _stop = true;
        _thread?.Join(3000);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Captures from a recording device, or from one app via process loopback.</summary>
public sealed unsafe class CaptureStream : StreamBase
{
    public delegate void DataHandler(ReadOnlySpan<float> data, int channels, double firstFrameTime, bool silent);

    private readonly string? _deviceId;
    private readonly int _pid;
    private readonly int _loopRate, _loopChannels;
    private readonly DataHandler _handler;

    public long Packets { get; private set; }
    public long Discontinuities { get; private set; }

    private CaptureStream(string? deviceId, int pid, int rate, int ch, DataHandler handler)
    {
        _deviceId = deviceId; _pid = pid; _loopRate = rate; _loopChannels = ch; _handler = handler;
    }

    protected override string Name => _deviceId != null ? "WinMusic capture (device)" : $"WinMusic capture (pid {_pid})";

    public static CaptureStream ForDevice(string deviceId, DataHandler handler) => new(deviceId, 0, 0, 0, handler);

    /// <summary>Captures everything an app (and its child processes) plays, at the requested format.</summary>
    public static CaptureStream ForProcess(int pid, int sampleRate, int channels, DataHandler handler) =>
        new(null, pid, sampleRate, channels, handler);

    protected override void Run(Action ready)
    {
        IMMDevice? dev = null;
        IAudioClient client;
        IntPtr fmt;
        uint flags = Native.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | Native.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;

        if (_deviceId != null)
        {
            dev = Devices.GetDevice(_deviceId);
            client = Devices.ActivateClient(dev);
            Hr.Check(client.GetMixFormat(out IntPtr mixPtr), "GetMixFormat");
            var mix = WaveFormatInfo.Parse(mixPtr);
            Marshal.FreeCoTaskMem(mixPtr);
            fmt = WaveFormatInfo.AllocFloat(mix.SampleRate, mix.Channels);
            flags |= Native.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Format = new WaveFormatInfo(mix.SampleRate, mix.Channels, 32, 32, true);
        }
        else
        {
            client = ProcessLoopback.Activate((uint)_pid, includeTree: true);
            fmt = WaveFormatInfo.AllocFloat(_loopRate, _loopChannels);
            flags |= Native.AUDCLNT_STREAMFLAGS_LOOPBACK;
            Format = new WaveFormatInfo(_loopRate, _loopChannels, 32, 32, true);
        }

        IntPtr evt = Native.CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        IAudioCaptureClient? cap = null;
        try
        {
            Hr.Check(client.Initialize(Native.AUDCLNT_SHAREMODE_SHARED, flags, 200_000, 0, fmt, IntPtr.Zero), "IAudioClient.Initialize (capture)");
            Hr.Check(client.SetEventHandle(evt), "SetEventHandle");
            var iid = Native.IID_IAudioCaptureClient;
            Hr.Check(client.GetService(ref iid, out object o), "GetService(IAudioCaptureClient)");
            cap = (IAudioCaptureClient)o;
            Hr.Check(client.Start(), "IAudioClient.Start (capture)");
            ready();

            int ch = Format.Channels;
            double rate = Format.SampleRate;
            var silence = new float[ch * 4800];
            while (!StopRequested)
            {
                Native.WaitForSingleObject(evt, 100);
                while (true)
                {
                    int hr = cap.GetNextPacketSize(out uint next);
                    Hr.Check(hr, "GetNextPacketSize");
                    if (next == 0) break;
                    Hr.Check(cap.GetBuffer(out IntPtr data, out uint frames, out uint bflags, out _, out ulong qpc), "Capture GetBuffer");
                    double now = AudioClock.Now;
                    double t = qpc != 0 && (bflags & 0x4) == 0 ? qpc * 1e-7 : now - frames / rate;
                    if (Math.Abs(t - now) > 0.5) t = now - frames / rate;
                    if ((bflags & Native.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0 && Packets > 0) Discontinuities++;
                    Packets++;
                    if ((bflags & Native.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
                    {
                        int n = (int)frames * ch;
                        if (silence.Length < n) silence = new float[n];
                        _handler(silence.AsSpan(0, n), ch, t, true);
                    }
                    else
                    {
                        _handler(new ReadOnlySpan<float>((void*)data, (int)frames * ch), ch, t, false);
                    }
                    Hr.Check(cap.ReleaseBuffer(frames), "Capture ReleaseBuffer");
                }
            }
            client.Stop();
        }
        finally
        {
            if (cap != null) Marshal.ReleaseComObject(cap);
            Marshal.ReleaseComObject(client);
            if (dev != null) Marshal.ReleaseComObject(dev);
            Marshal.FreeHGlobal(fmt);
            Native.CloseHandle(evt);
        }
    }
}

/// <summary>Plays audio to a device in shared mode, pulling stereo float from a callback.</summary>
public sealed unsafe class RenderStream : StreamBase
{
    /// <param name="queuedFrames">Frames still waiting in the device buffer (not yet played).</param>
    public delegate void FillHandler(Span<float> stereo, int frames, double now, int queuedFrames);

    private readonly string _deviceId;
    private readonly FillHandler _fill;
    public int BufferFrames { get; private set; }
    public double LatencyMs { get; private set; }
    protected override string Name => "WinMusic render";

    private readonly float _sessionVolume;

    /// <param name="sessionVolume">Windows per-app volume for this stream's session (1 = untouched).</param>
    public RenderStream(string deviceId, FillHandler fill, float sessionVolume = 1f) { _deviceId = deviceId; _fill = fill; _sessionVolume = sessionVolume; }

    protected override void Run(Action ready)
    {
        var dev = Devices.GetDevice(_deviceId);
        var client = Devices.ActivateClient(dev);
        Hr.Check(client.GetMixFormat(out IntPtr mixPtr), "GetMixFormat");
        var mix = WaveFormatInfo.Parse(mixPtr);
        Marshal.FreeCoTaskMem(mixPtr);
        int ch = mix.Channels;
        Format = new WaveFormatInfo(mix.SampleRate, ch, 32, 32, true);
        IntPtr fmt = WaveFormatInfo.AllocFloat(mix.SampleRate, ch);
        IntPtr evt = Native.CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        IAudioRenderClient? rc = null;
        try
        {
            // NOPERSIST: Windows must not remember a lowered/muted per-app volume for us; we always start at exactly 100 %.
            uint flags = Native.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | Native.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                         Native.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY | Native.AUDCLNT_STREAMFLAGS_NOPERSIST;
            Hr.Check(client.Initialize(Native.AUDCLNT_SHAREMODE_SHARED, flags, 200_000, 0, fmt, IntPtr.Zero), "IAudioClient.Initialize (render)");
            Hr.Check(client.GetBufferSize(out uint bufferFrames), "GetBufferSize");
            BufferFrames = (int)bufferFrames;
            if (client.GetStreamLatency(out long lat) == 0) LatencyMs = lat / 10_000.0;
            Hr.Check(client.SetEventHandle(evt), "SetEventHandle");
            var iid = Native.IID_IAudioRenderClient;
            Hr.Check(client.GetService(ref iid, out object o), "GetService(IAudioRenderClient)");
            rc = (IAudioRenderClient)o;

            if (_sessionVolume != 1f)
            {
                var vid = Native.IID_ISimpleAudioVolume;
                if (client.GetService(ref vid, out object vo) == 0)
                {
                    var ctx = Guid.Empty;
                    ((ISimpleAudioVolume)vo).SetMasterVolume(_sessionVolume, ref ctx);
                    Marshal.ReleaseComObject(vo);
                }
            }

            // start with a buffer of silence
            Hr.Check(rc.GetBuffer(bufferFrames, out _), "Render GetBuffer");
            Hr.Check(rc.ReleaseBuffer(bufferFrames, Native.AUDCLNT_BUFFERFLAGS_SILENT), "Render ReleaseBuffer");
            Hr.Check(client.Start(), "IAudioClient.Start (render)");
            ready();

            var stereo = new float[bufferFrames * 2];
            while (!StopRequested)
            {
                Native.WaitForSingleObject(evt, 200);
                Hr.Check(client.GetCurrentPadding(out uint padding), "GetCurrentPadding");
                int frames = (int)(bufferFrames - padding);
                if (frames <= 0) continue;
                var span = stereo.AsSpan(0, frames * 2);
                _fill(span, frames, AudioClock.Now, (int)padding);
                Hr.Check(rc.GetBuffer((uint)frames, out IntPtr data), "Render GetBuffer");
                float* d = (float*)data;
                if (ch == 2)
                {
                    span.CopyTo(new Span<float>(d, frames * 2));
                }
                else if (ch == 1)
                {
                    for (int i = 0; i < frames; i++) d[i] = 0.5f * (span[i * 2] + span[i * 2 + 1]);
                }
                else
                {
                    for (int i = 0; i < frames; i++)
                    {
                        float* f = d + i * ch;
                        f[0] = span[i * 2]; f[1] = span[i * 2 + 1];
                        for (int c = 2; c < ch; c++) f[c] = 0;
                    }
                }
                Hr.Check(rc.ReleaseBuffer((uint)frames, 0), "Render ReleaseBuffer");
            }
            client.Stop();
        }
        finally
        {
            if (rc != null) Marshal.ReleaseComObject(rc);
            Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(dev);
            Marshal.FreeHGlobal(fmt);
            Native.CloseHandle(evt);
        }
    }
}
