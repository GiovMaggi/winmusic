using System.Diagnostics;
using System.Globalization;
using System.Text;
using WinMusic.Audio.Engine;
using WinMusic.Audio.Platform;
using WinMusic.Audio.Wasapi;
using WinMusic.Core.Analysis;

// WinMusic on-device self test.
// Runs on a real Windows audio stack with VB-CABLE installed (GitHub Actions does this) and
// measures what WinMusic actually produces.
//
//   WinMusic.SelfTest.exe [--report file.md]
//   WinMusic.SelfTest.exe --tone <deviceId> <freq> <seconds> <sessionVolume>   (child tone player)
//   WinMusic.SelfTest.exe --tone-cable <freq> <seconds> <sessionVolume>        (child tone player → CABLE Input)

var inv = CultureInfo.InvariantCulture;
if (args.Length > 0 && args[0] == "--tone-cable")
    return ToneChild(Devices.FindCableInput()!.Id, double.Parse(args[1], inv), double.Parse(args[2], inv), float.Parse(args[3], inv));
if (args.Length > 0 && args[0] == "--tone-pattern") // deviceId, then play/pause seconds alternating
{
    var secs = args.Skip(2).Select(a => double.Parse(a, inv)).ToArray();
    for (int i = 0; i < secs.Length; i++)
    {
        if (i % 2 == 0) ToneChild(args[1], 1000, secs[i], 1f);   // play (opens and closes its stream, like pausing Spotify)
        else Thread.Sleep(TimeSpan.FromSeconds(secs[i]));       // paused: no audio stream at all
    }
    return 0;
}
if (args.Length > 0 && args[0] == "--tone")
    return ToneChild(args[1], double.Parse(args[2], inv), double.Parse(args[3], inv), float.Parse(args[4], inv));

string reportPath = args.Length >= 2 && args[0] == "--report" ? args[1] : "selftest-report.md";
var report = new StringBuilder();
void Flush() { lock (report) File.WriteAllText(reportPath, report.ToString()); }
void Say(string s) { Console.WriteLine(s); lock (report) report.AppendLine(s); }
bool allOk = true;
void Check(bool ok, string what) { Say($"- {(ok ? "PASS" : "FAIL")}: {what}"); if (!ok) allOk = false; }
AppDomain.CurrentDomain.UnhandledException += (_, e) => { Say($"\n**CRASH: {e.ExceptionObject}**"); Flush(); };

// A separately named copy of this exe acts as "the music app" (like Spotify.exe) so that
// capturing it by name never captures the test itself.
string toneApp = Path.Combine(Path.GetTempPath(), "WinMusicToneApp", "ToneApp.exe");
Directory.CreateDirectory(Path.GetDirectoryName(toneApp)!);
File.Copy(Environment.ProcessPath!, toneApp, true);

Say("# WinMusic self test");
Say($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");
Say("");
Say("## Devices");
foreach (var d in Devices.List(DeviceFlow.Playback)) Say($"- playback: {d.Name}");
foreach (var d in Devices.List(DeviceFlow.Recording)) Say($"- recording: {d.Name}");

Say("");
Say("## Offline VB-CABLE package");
{
    var dir = Path.Combine(Path.GetTempPath(), "WinMusicPkgTest");
    Directory.CreateDirectory(dir);
    var zip = SystemSetup.GetCablePackage(dir, new Progress<string>(m => Say($"  - {m}"))).GetAwaiter().GetResult();
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();
    Check(hash == "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90", "embedded package is the official, unmodified VB-CABLE zip");
    using var z = System.IO.Compression.ZipFile.OpenRead(zip);
    Check(z.Entries.Any(e => e.Name == "VBCABLE_Setup_x64.exe"), "package contains VBCABLE_Setup_x64.exe");
}

Say("");
var status = SystemSetup.GetCableStatus();
Check(status.Installed, "VB-CABLE detected");
if (!status.Installed) { Flush(); return 2; }
string cableIn = status.Input!.Id, cableOut = status.Output!.Id;

Say("");
Say("## Optimize");
Devices.SetEndpointVolume(cableOut, 0.5f);   // simulate a badly configured cable
Say($"- before: CABLE Output Windows volume {Devices.GetEndpointVolume(cableOut).volume * 100:F0}%, status says volume problem: {SystemSetup.GetCableStatus().CableVolumeNot100}");
foreach (var line in SystemSetup.Optimize(null)) Say($"- {line}");
status = SystemSetup.GetCableStatus();
Say($"- after: CABLE Input {status.InputFormat}, CABLE Output {status.OutputFormat}");
Check(status.InputFormat?.SampleRate == 48000 && status.OutputFormat?.SampleRate == 48000, "cable runs at 48 kHz");
Check(Devices.GetEndpointVolume(cableOut).volume > 0.999f && Devices.GetEndpointVolume(cableIn).volume > 0.999f, "Optimize restored cable volumes to 100%");
Check(SystemSetup.IsDuckingDisabled(), "Windows call ducking disabled");
Check(!status.CableVolumeNot100, "status reports no volume problem");

int outRate = Devices.GetMixFormat(cableIn).SampleRate;
int recRate = Devices.GetMixFormat(cableOut).SampleRate;

// ---------------------------------------------------------------- 0
Say("");
Say("## 0. Baseline: VB-CABLE by itself (tone app → CABLE Input → CABLE Output, no WinMusic)");
{
    using var child = StartTone(cableIn, 9, 1f);
    Thread.Sleep(1000);
    var rec = RecordDevice(cableOut, 6000);
    var rep = SineAnalyzer.Analyze(TrimSilence(rec), recRate, 1000);
    Say($"- {rep}");
    Say("- (informational: the quality limit of the virtual cable itself)");
    Kill(child);
}

// ---------------------------------------------------------------- A
Say("");
Say("## A. Per-app capture (process loopback) of a 1 kHz tone");
{
    using var child = StartTone(cableIn, 10, 1f);
    Thread.Sleep(1000);
    var (rec, info) = RecordProcess(child.Id, outRate, 5000);
    Say($"- capture format {info.fmt}, packets {info.packets}, discontinuities {info.disc}");
    var rep = SineAnalyzer.Analyze(TrimSilence(rec), outRate, 1000);
    Say($"- {rep}");
    Check(rep.Blocks >= 30, "received at least 3 s of audio");
    Check(rep.GlitchBlocks == 0, "no glitches");
    Check(rep.MedianThdNDb < -120, $"THD+N below -120 dB ({rep.MedianThdNDb:F1} dB)");
    Check(Math.Abs(20 * Math.Log10(rep.Amplitude / 0.5)) < 0.05, "level unchanged (bit-transparent gain)");
    Kill(child);
}

// ---------------------------------------------------------------- A2
Say("");
Say("## A2. Which Windows volume controls change what friends hear? (informational)");
{
    using var child = StartTone(cableIn, 12, 1f);
    Thread.Sleep(1000);
    var samples = new List<float>();
    var cap = CaptureStream.ForProcess(child.Id, outRate, 2, (d, ch, t, silent) => { lock (samples) for (int i = 0; i < d.Length; i += ch) samples.Add(d[i]); });
    cap.Start();
    Thread.Sleep(2500);
    int mark; lock (samples) mark = samples.Count;
    Devices.SetEndpointVolume(cableIn, 0.25f);    // the output device's volume slider (like your speaker volume)
    Thread.Sleep(2500);
    cap.Dispose();
    Devices.SetEndpointVolume(cableIn, 1f);
    float[] all; lock (samples) all = samples.ToArray();
    var before = SineAnalyzer.Analyze(TrimSilence(all[..mark]), outRate, 1000);
    var after = SineAnalyzer.Analyze(all[Math.Min(all.Length, mark + outRate / 2)..], outRate, 1000);
    double delta = 20 * Math.Log10(Math.Max(after.Amplitude, 1e-9) / Math.Max(before.Amplitude, 1e-9));
    Say($"- output device volume 100% → 25%: captured level changed by {delta:F1} dB → " +
        (Math.Abs(delta) < 0.1 ? "speaker volume does NOT affect what friends hear" : "speaker volume DOES affect what friends hear"));
    Kill(child);
}
{
    using var child = StartTone(cableIn, 8, 0f);   // the app's own Windows per-app volume at 0
    Thread.Sleep(1000);
    var (rec, _) = RecordProcess(child.Id, outRate, 3000);
    double peak = rec.Length > 0 ? rec.Max(Math.Abs) : 0;
    Say($"- app's per-app volume at 0 → captured peak {20 * Math.Log10(Math.Max(peak, 1e-9)):F1} dBFS → " +
        (peak > 0.1 ? "per-app volume does NOT affect what friends hear" : "per-app volume DOES affect what friends hear"));
    Kill(child);
}

// ---------------------------------------------------------------- B
const int SettleSeconds = 12; // drift correction measures for 6 s, then ramps the correction in over 3 s
Say("");
Say("## B. Engine with a source on a different clock (drift correction), measured before and after VB-CABLE");
{
    using var engine = new AudioEngine();
    engine.Log += m => Say($"  - log: {m}");
    engine.SetMicDevice("");
    engine.Start(cableIn);
    var gen = engine.AddExternalSource("generator", 48000);
    var stopGen = false;
    var genThread = new Thread(() =>
    {
        // Paced by the QPC clock, i.e. NOT the cable's clock: a realistic second device.
        long phase = 0; const int packet = 480; const double fs = 48000;
        var buf = new float[packet * 2];
        double t0 = AudioClock.Now; long written = 0;
        while (!Volatile.Read(ref stopGen))
        {
            double due = t0 + (written + packet) / fs;
            while (AudioClock.Now < due) Thread.Sleep(1);
            for (int i = 0; i < packet; i++)
            {
                float v = (float)(0.5 * Math.Sin(2 * Math.PI * 997 * phase++ / fs));
                buf[i * 2] = v; buf[i * 2 + 1] = v;
            }
            gen.Write(buf, 2, t0 + written / fs);
            written += packet;
        }
    }) { IsBackground = true, Priority = ThreadPriority.Highest };
    genThread.Start();

    var pre = new List<float>();
    var selfCap = CaptureStream.ForProcess(Environment.ProcessId, outRate, 2, (d, ch, t, silent) => { lock (pre) for (int i = 0; i < d.Length; i += ch) pre.Add(d[i]); });
    selfCap.Start();
    var rec = RecordDevice(cableOut, (SettleSeconds + 10) * 1000);
    selfCap.Dispose();
    var st = engine.GetStatus().First();
    Volatile.Write(ref stopGen, true); genThread.Join();
    Say($"- engine output {engine.OutputFormat}, source: {st}");
    float[] preArr; lock (pre) preArr = pre.ToArray();
    var preRep = SineAnalyzer.Analyze(Settled(TrimSilence(preArr), outRate), outRate, 997);
    var rep = SineAnalyzer.Analyze(Settled(TrimSilence(rec), recRate), recRate, 997);
    Say($"- WinMusic output (before VB-CABLE): {preRep}");
    Say($"- virtual mic (after VB-CABLE):      {rep}");
    Check(preRep.Blocks >= 80 && rep.Blocks >= 80, "audio flowing for 10 s");
    Check(preRep.GlitchBlocks == 0 && preRep.SilentBlocks == 0 && rep.GlitchBlocks == 0 && rep.SilentBlocks == 0, "no glitches or dropouts");
    Check(st.Underruns == 0, "no buffer underruns");
    Check(preRep.MedianThdNDb < -110, $"WinMusic output THD+N below -110 dB ({preRep.MedianThdNDb:F1} dB)");
    Check(rep.MedianThdNDb < -100, $"virtual mic THD+N below -100 dB ({rep.MedianThdNDb:F1} dB)");
    Check(Math.Abs(20 * Math.Log10(rep.Amplitude / 0.5)) < 0.1, "level unchanged end to end");
    engine.Stop();
}

// ---------------------------------------------------------------- C
Say("");
Say("## C. Full chain: real app → WinMusic per-app capture → mix → virtual mic");
{
    using var child = StartTone(cableIn, SettleSeconds + 16, 1f);
    Thread.Sleep(800);
    using var engine = new AudioEngine();
    engine.Log += m => Say($"  - log: {m}");
    engine.SetMicDevice("");
    engine.Start(cableIn);
    engine.AddAppPid(child.Id);
    var tap = new List<float>();
    engine.OutputTap = d => { lock (tap) for (int i = 0; i < d.Length; i += 2) tap.Add(d[i]); };
    var rec = RecordDevice(cableOut, (SettleSeconds + 10) * 1000);
    engine.OutputTap = null;
    var st = engine.GetStatus().FirstOrDefault();
    Say($"- source: {st}");
    float[] tapArr; lock (tap) tapArr = tap.ToArray();
    var rep = SineAnalyzer.Analyze(Settled(TrimSilence(tapArr), outRate), outRate, 1000);
    Say($"- WinMusic output (exactly what is sent to the cable): {rep}");
    double micPeak = rec.Length > 0 ? rec.Max(Math.Abs) : 0;
    Say($"- virtual mic peak {20 * Math.Log10(Math.Max(micPeak, 1e-9)):F1} dBFS (also contains the app's direct sound, so not analysed)");
    Check(rep.Blocks >= 80, "app audio flowed through WinMusic for 10 s");
    Check(rep.GlitchBlocks == 0 && rep.SilentBlocks == 0, "no glitches or dropouts");
    Check(st?.Underruns == 0, "no buffer underruns");
    Check(rep.MedianThdNDb < -110, $"THD+N below -110 dB ({rep.MedianThdNDb:F1} dB)");
    Check(Math.Abs(20 * Math.Log10(rep.Amplitude / 0.5)) < 0.1, "level unchanged (0 dB = untouched)");
    Check(micPeak > 0.1, "sound reaches the virtual mic");
    engine.Stop();
    Kill(child);
}

// ---------------------------------------------------------------- D
Say("");
Say("## D. Picking an app by name; it closes and reopens while WinMusic runs");
{
    using var engine = new AudioEngine();
    engine.Log += m => Say($"  - log: {m}");
    engine.SetMicDevice("");
    engine.Start(cableIn);
    engine.SetApps(new[] { "ToneApp.exe" });
    Thread.Sleep(3000);
    Check(!engine.GetStatus().Any(s => s.Kind == SourceKind.App), "nothing captured while the app isn't running");
    var child = StartTone(cableIn, 8, 1f);
    Thread.Sleep(5000);
    var s2 = engine.GetStatus();
    Check(s2.Any(s => s.Kind == SourceKind.App && s.Pid == child.Id), $"attached to ToneApp.exe when it started (pid {child.Id})");
    Kill(child);
    Thread.Sleep(5000);
    Check(!engine.GetStatus().Any(s => s.Kind == SourceKind.App), "detached when the app closed");
    var child2 = StartTone(cableIn, 8, 1f);
    Thread.Sleep(5000);
    var s4 = engine.GetStatus();
    Check(s4.Any(s => s.Kind == SourceKind.App && s.Pid == child2.Id), $"re-attached after the app reopened (pid {child2.Id})");
    engine.Stop();
    Kill(child2);
}

// ---------------------------------------------------------------- E
Say("");
Say("## E. Music paused and resumed (app closes its audio stream for 4 s, twice)");
{
    var psi = new ProcessStartInfo(toneApp) { UseShellExecute = false };
    foreach (var a in new[] { "--tone-pattern", cableIn, "14", "4", "10", "4", "12" }) psi.ArgumentList.Add(a);
    using var child = Process.Start(psi)!;
    using var engine = new AudioEngine();
    engine.SetMicDevice("");
    engine.Start(cableIn);
    engine.AddAppPid(child.Id);
    var tap = new List<float>();
    engine.OutputTap = d => { lock (tap) for (int i = 0; i < d.Length; i += 2) tap.Add(d[i]); };
    child.WaitForExit(60000);
    Thread.Sleep(1000);
    engine.OutputTap = null;
    var st = engine.GetStatus().FirstOrDefault();
    Say($"- source: {st}");
    float[] all; lock (tap) all = tap.ToArray();
    var whole = SineAnalyzer.Analyze(TrimSilence(all), outRate, 1000);
    Say($"- whole session: {whole}");
    var trimmed = TrimSilence(all);
    var lastPart = trimmed.Length > outRate * 9 ? trimmed[^(outRate * 8)..] : trimmed;
    var tail = SineAnalyzer.Analyze(lastPart, outRate, 1000);
    Say($"- last 8 s after the second resume: {tail}");
    Check(st != null && st.Underruns >= 2, "both pauses were detected");
    Check(whole.SilentBlocks >= 50, "output is silent while paused");
    Check(whole.GlitchBlocks <= 4, $"only the stop/start edges differ ({whole.GlitchBlocks} edge blocks)");
    Check(tail.GlitchBlocks == 0 && tail.SilentBlocks == 0 && tail.MedianThdNDb < -110, $"clean audio after resuming ({tail.MedianThdNDb:F1} dB)");
    engine.Stop();
}

// ---------------------------------------------------------------- F
const int SoakMinutes = 5;
Say("");
Say($"## F. Soak test: {SoakMinutes} minutes of continuous music");
{
    using var child = StartTone(cableIn, SoakMinutes * 60 + 30, 1f);
    Thread.Sleep(800);
    using var engine = new AudioEngine();
    engine.SetMicDevice("");
    engine.Start(cableIn);
    engine.AddAppPid(child.Id);
    var tap = new List<float>(outRate * (SoakMinutes * 60 + 20));
    engine.OutputTap = d => { lock (tap) for (int i = 0; i < d.Length; i += 2) tap.Add(d[i]); };
    for (int m = 1; m <= SoakMinutes; m++)
    {
        Thread.Sleep(60_000);
        var s1 = engine.GetStatus().FirstOrDefault();
        Say($"  - minute {m}: buffered {s1?.BufferedMs:F1} ms, clock {s1?.CorrectionPpm:F1} ppm, underruns {s1?.Underruns}, resyncs {s1?.Resyncs}");
    }
    engine.OutputTap = null;
    var st = engine.GetStatus().FirstOrDefault();
    float[] all; lock (tap) all = tap.ToArray();
    var rep = SineAnalyzer.Analyze(Settled(TrimSilence(all), outRate), outRate, 1000);
    Say($"- {rep}");
    Check(rep.Blocks >= SoakMinutes * 590, "audio for the whole session");
    Check(rep.GlitchBlocks == 0 && rep.SilentBlocks == 0, "no glitches or dropouts in the whole session");
    Check(st?.Underruns == 0 && st?.Resyncs == 0 && st?.Overflows == 0, "no underruns, resyncs or overflows");
    Check(rep.MedianThdNDb < -110 && rep.WorstThdNDb < -90, $"THD+N median {rep.MedianThdNDb:F1} dB, worst {rep.WorstThdNDb:F1} dB");
    engine.Stop();
    Kill(child);
}

Say("");
Say(allOk ? "**RESULT: ALL PASSED**" : "**RESULT: FAILURES**");
Flush();
return allOk ? 0 : 1;

// ---------------------------------------------------------------- helpers

static float[] Settled(float[] a, int rate) => a.Length > rate * 12 ? a[(rate * 12)..] : Array.Empty<float>();

static void Kill(Process p)
{
    try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { }
}

Process StartTone(string deviceId, double seconds, float sessionVolume)
{
    var psi = new ProcessStartInfo(toneApp) { UseShellExecute = false };
    foreach (var a in new[] { "--tone", deviceId, "1000", seconds.ToString(inv), sessionVolume.ToString(inv) })
        psi.ArgumentList.Add(a);
    return Process.Start(psi)!;
}

static int ToneChild(string deviceId, double freq, double seconds, float sessionVolume)
{
    long phase = 0;
    int rate = Devices.GetMixFormat(deviceId).SampleRate;
    using var r = new RenderStream(deviceId, (span, frames, now, queued) =>
    {
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(0.5 * Math.Sin(2 * Math.PI * freq * phase++ / rate));
            span[i * 2] = v; span[i * 2 + 1] = v;
        }
    }, sessionVolume);
    r.Start();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    return 0;
}

static float[] RecordDevice(string deviceId, int ms)
{
    var rec = new List<float>();
    using (var cap = CaptureStream.ForDevice(deviceId, (d, ch, t, silent) => { lock (rec) for (int i = 0; i < d.Length; i += ch) rec.Add(d[i]); }))
    {
        cap.Start();
        Thread.Sleep(ms);
    }
    lock (rec) return rec.ToArray();
}

static (float[] data, (WaveFormatInfo? fmt, long packets, long disc) info) RecordProcess(int pid, int rate, int ms)
{
    var rec = new List<float>();
    var cap = CaptureStream.ForProcess(pid, rate, 2, (d, ch, t, silent) => { lock (rec) for (int i = 0; i < d.Length; i += ch) rec.Add(d[i]); });
    cap.Start();
    Thread.Sleep(ms);
    cap.Dispose();
    lock (rec) return (rec.ToArray(), (cap.Format, cap.Packets, cap.Discontinuities));
}

static float[] TrimSilence(float[] a)
{
    int s = 0, e = a.Length;
    while (s < e && Math.Abs(a[s]) < 1e-4) s++;
    while (e > s && Math.Abs(a[e - 1]) < 1e-4) e--;
    return a[s..e];
}
