using System.Diagnostics;
using System.Text;
using WinMusic.Audio.Engine;
using WinMusic.Audio.Platform;
using WinMusic.Audio.Wasapi;
using WinMusic.Core.Analysis;

// WinMusic on-device self test.
// Runs on a real Windows audio stack with VB-CABLE installed (GitHub Actions does this) and
// measures what actually comes out of the virtual microphone.
//
//   WinMusic.SelfTest.exe [--report file.md]
//   WinMusic.SelfTest.exe --tone <deviceId> <freq> <seconds> <sessionVolume>   (internal: child tone player)

if (args.Length > 0 && args[0] == "--tone")
    return ToneChild(args[1], double.Parse(args[2]), double.Parse(args[3]), float.Parse(args[4]));

string reportPath = args.Length >= 2 && args[0] == "--report" ? args[1] : "selftest-report.md";
var report = new StringBuilder();
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    report.AppendLine().AppendLine($"**CRASH: {e.ExceptionObject}**");
    File.WriteAllText(reportPath, report.ToString());
};
void Say(string s) { Console.WriteLine(s); report.AppendLine(s); }
bool allOk = true;
void Check(bool ok, string what) { Say($"- {(ok ? "PASS" : "FAIL")}: {what}"); if (!ok) allOk = false; }

Say("# WinMusic self test");
Say($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");
Say("");
Say("## Devices");
foreach (var d in Devices.List(DeviceFlow.Playback)) Say($"- playback: {d.Name}");
foreach (var d in Devices.List(DeviceFlow.Recording)) Say($"- recording: {d.Name}");

var status = SystemSetup.GetCableStatus();
Check(status.Installed, "VB-CABLE detected");
if (!status.Installed) { File.WriteAllText(reportPath, report.ToString()); return 2; }
Say($"- CABLE Input device format: {status.InputFormat}, mix format: {Devices.GetMixFormat(status.Input!.Id)}");
Say($"- CABLE Output device format: {status.OutputFormat}, mix format: {Devices.GetMixFormat(status.Output!.Id)}");

Say("");
Say("## Optimize");
foreach (var line in SystemSetup.Optimize(null)) Say($"- {line}");
status = SystemSetup.GetCableStatus();
Say($"- after: CABLE Input {status.InputFormat}, CABLE Output {status.OutputFormat}");

const double Fs = 48000;
string cableIn = status.Input!.Id, cableOut = status.Output!.Id;
int outRate = Devices.GetMixFormat(cableIn).SampleRate;
int recRate = Devices.GetMixFormat(cableOut).SampleRate;

// ---------------------------------------------------------------- A
Say("");
Say("## A. Per-app capture (process loopback) of a 1 kHz tone");
{
    using var child = StartTone(cableIn, 1000, 10, 1f);
    Thread.Sleep(1000);
    var rec = new List<float>();
    using (var cap = CaptureStream.ForProcess(child.Id, outRate, 2, (d, ch, t, silent) =>
           { lock (rec) for (int i = 0; i < d.Length; i += ch) rec.Add(d[i]); }))
    {
        cap.Start();
        Thread.Sleep(5000);
        Say($"- capture format {cap.Format}, packets {cap.Packets}, discontinuities {cap.Discontinuities}");
    }
    float[] a; lock (rec) a = TrimSilence(rec.ToArray());
    var rep = SineAnalyzer.Analyze(a, outRate, 1000);
    Say($"- {rep}");
    Check(rep.Blocks >= 30, "received at least 3 s of audio");
    Check(rep.GlitchBlocks == 0, "no glitches");
    Check(rep.MedianThdNDb < -100, $"THD+N below -100 dB ({rep.MedianThdNDb:F1} dB)");
    Check(Math.Abs(20 * Math.Log10(rep.Amplitude / 0.5)) < 0.1, "level unchanged (bit-transparent gain)");
    child.Kill(); child.WaitForExit();
}

// ---------------------------------------------------------------- B
Say("");
Say("## B. Engine → CABLE Input → CABLE Output, from a source on a different clock (drift correction)");
{
    using var engine = new AudioEngine();
    engine.Log += m => Say($"  - log: {m}");
    engine.SetMicDevice("");
    engine.Start(cableIn);
    var gen = engine.AddExternalSource("generator", (int)Fs);
    var stopGen = false;
    var genThread = new Thread(() =>
    {
        // Paced by the QPC clock, i.e. NOT the cable's clock: a realistic second device.
        long phase = 0; const int packet = 480;
        var buf = new float[packet * 2];
        double t0 = AudioClock.Now; long written = 0;
        while (!stopGen)
        {
            double due = t0 + (written + packet) / Fs;
            while (AudioClock.Now < due) Thread.Sleep(1);
            for (int i = 0; i < packet; i++)
            {
                float v = (float)(0.5 * Math.Sin(2 * Math.PI * 997 * phase++ / Fs));
                buf[i * 2] = v; buf[i * 2 + 1] = v;
            }
            gen.Write(buf, 2, t0 + written / Fs);
            written += packet;
        }
    }) { IsBackground = true, Priority = ThreadPriority.Highest };
    genThread.Start();

    var rec = Record(cableOut, 14000);
    stopGen = true; genThread.Join();
    var st = engine.GetStatus().First();
    Say($"- engine output {engine.OutputFormat}, source buffered {st.BufferedMs:F1} ms, clock correction {st.CorrectionPpm:F1} ppm, underruns {st.Underruns}");
    var settled = rec.Skip(recRate * 4).ToArray(); // let the drift loop lock in
    var rep = SineAnalyzer.Analyze(settled, recRate, 997);
    Say($"- {rep}");
    Check(rep.Blocks >= 80, "received audio from the virtual mic");
    Check(rep.GlitchBlocks == 0 && rep.SilentBlocks == 0, "no glitches or dropouts");
    Check(rep.MedianThdNDb < -100, $"THD+N below -100 dB ({rep.MedianThdNDb:F1} dB)");
    Check(Math.Abs(20 * Math.Log10(rep.Amplitude / 0.5)) < 0.1, "level unchanged");
    engine.Stop();
}

// ---------------------------------------------------------------- C
Say("");
Say("## C. Full chain: app → WinMusic → virtual mic");
{
    // The tone app plays with its Windows per-app volume at 0, so the only way the tone can
    // reach CABLE Output is through WinMusic. This also tells us whether per-app volume affects capture.
    using var child = StartTone(cableIn, 1000, 16, 0f);
    Thread.Sleep(800);
    using var engine = new AudioEngine();
    engine.Log += m => Say($"  - log: {m}");
    engine.SetMicDevice("");
    engine.Start(cableIn);
    engine.AddAppPid(child.Id);
    var rec = Record(cableOut, 12000);
    var st = engine.GetStatus().FirstOrDefault();
    Say($"- source: {st}");
    var settled = rec.Skip(recRate * 3).ToArray();
    var rep = SineAnalyzer.Analyze(settled, recRate, 1000);
    Say($"- {rep}");
    if (rep.Blocks == 0)
    {
        Say("- NOTE: with the app's Windows volume at 0, nothing was captured → per-app volume DOES affect what friends hear.");
        engine.Stop(); child.Kill(); child.WaitForExit();
        using var child2 = StartTone(cableIn, 1000, 16, 1f);
        Thread.Sleep(800);
        using var engine2 = new AudioEngine();
        engine2.SetMicDevice("");
        engine2.Start(cableIn);
        engine2.AddAppPid(child2.Id);
        rec = Record(cableOut, 12000);
        rep = SineAnalyzer.Analyze(rec.Skip(recRate * 3).ToArray(), recRate, 1000);
        Say($"- retry at full app volume (direct + WinMusic paths overlap, same frequency): {rep}");
        engine2.Stop(); child2.Kill(); child2.WaitForExit();
    }
    else
    {
        Say($"- NOTE: captured at {20 * Math.Log10(rep.Amplitude / 0.5):F1} dB relative to the app's own output with its Windows volume at 0 → per-app volume does NOT affect what friends hear.");
        engine.Stop(); child.Kill(); child.WaitForExit();
    }
    Check(rep.Blocks >= 60, "app audio reached the virtual mic");
    Check(rep.GlitchBlocks == 0 && rep.SilentBlocks == 0, "no glitches or dropouts");
    Check(rep.MedianThdNDb < -100, $"THD+N below -100 dB ({rep.MedianThdNDb:F1} dB)");
}

Say("");
Say(allOk ? "**RESULT: ALL PASSED**" : "**RESULT: FAILURES**");
File.WriteAllText(reportPath, report.ToString());
return allOk ? 0 : 1;

// ---------------------------------------------------------------- helpers

static Process StartTone(string deviceId, double freq, double seconds, float sessionVolume)
{
    var exe = Environment.ProcessPath!;
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
    foreach (var a in new[] { "--tone", deviceId, freq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                              seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                              sessionVolume.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        psi.ArgumentList.Add(a);
    return Process.Start(psi)!;
}

static int ToneChild(string deviceId, double freq, double seconds, float sessionVolume)
{
    long phase = 0;
    int rate = Devices.GetMixFormat(deviceId).SampleRate;
    using var r = new RenderStream(deviceId, (span, frames, now) =>
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

static float[] Record(string deviceId, int ms)
{
    var rec = new List<float>();
    using (var cap = CaptureStream.ForDevice(deviceId, (d, ch, t, silent) => { lock (rec) for (int i = 0; i < d.Length; i += ch) rec.Add(d[i]); }))
    {
        cap.Start();
        Thread.Sleep(ms);
    }
    lock (rec) return rec.ToArray();
}

static float[] TrimSilence(float[] a)
{
    int s = 0, e = a.Length;
    while (s < e && Math.Abs(a[s]) < 1e-4) s++;
    while (e > s && Math.Abs(a[e - 1]) < 1e-4) e--;
    return a[s..e];
}
