using WinMusic.Core.Analysis;
using WinMusic.Core.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WinMusic.Core.Tests;

public class DspTests
{
    private readonly ITestOutputHelper _out;
    public DspTests(ITestOutputHelper output) => _out = output;

    private static float[] Sine(int frames, double fs, double f, double amp, bool stereo = true)
    {
        int ch = stereo ? 2 : 1;
        var a = new float[frames * ch];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * f * i / fs));
            for (int c = 0; c < ch; c++) a[i * ch + c] = v;
        }
        return a;
    }

    private static float[] Left(float[] stereo, int skipFrames = 0)
    {
        int n = stereo.Length / 2 - skipFrames;
        var m = new float[n];
        for (int i = 0; i < n; i++) m[i] = stereo[(i + skipFrames) * 2];
        return m;
    }

    private static float[] RunResampler(float[] input, double step, int outFrames)
    {
        var rs = new SincResampler(step);
        var output = new float[outFrames * 2];
        int inPos = 0, block = 480;
        for (int o = 0; o < outFrames; o += block)
        {
            int n = Math.Min(block, outFrames - o);
            int need = rs.InputFramesNeeded(n, step);
            var span = rs.GetInputSpan(need);
            input.AsSpan(inPos * 2, need * 2).CopyTo(span);
            rs.CommitInput(need);
            inPos += need;
            rs.Produce(output.AsSpan(o * 2), n, step);
        }
        return output;
    }

    [Theory]
    [InlineData(48000, 48000, 1000, 1.0003)]   // drift correction only
    [InlineData(48000, 48000, 1000, 0.9996)]
    [InlineData(44100, 48000, 1000, 1.0)]       // Spotify-style 44.1k into a 48k cable
    [InlineData(44100, 48000, 15000, 1.0)]
    [InlineData(96000, 48000, 997, 1.0)]
    public void Resampler_IsTransparent(int inRate, int outRate, double freq, double drift)
    {
        double step = (double)inRate / outRate * drift;
        int outFrames = 48000;
        var input = Sine((int)(outFrames * step) + 1000, inRate, freq, 0.7);
        var output = RunResampler(input, step, outFrames);
        var mono = Left(output, 1000);
        var rep = SineAnalyzer.Analyze(mono, outRate, freq * drift);
        _out.WriteLine(rep.ToString());
        Assert.Equal(0, rep.GlitchBlocks);
        Assert.True(rep.WorstThdNDb < -110, $"THD+N {rep.WorstThdNDb:F1} dB");
        Assert.InRange(20 * Math.Log10(rep.Amplitude / 0.7), -0.01, 0.01);
    }

    [Fact]
    public void Resampler_FlatTo20kHz()
    {
        var input = Sine(60000, 48000, 20000, 0.5);
        var output = RunResampler(input, 1.0002, 48000);
        var rep = SineAnalyzer.Analyze(Left(output, 1000), 48000, 20000 * 1.0002);
        _out.WriteLine(rep.ToString());
        Assert.InRange(20 * Math.Log10(rep.Amplitude / 0.5), -0.05, 0.05);
        Assert.True(rep.WorstThdNDb < -100, $"THD+N {rep.WorstThdNDb:F1} dB");
    }

    [Fact]
    public void Resampler_RejectsAliasing()
    {
        // 30 kHz at 96k cannot exist at 48k; it must be removed, not folded down to 18 kHz.
        var input = Sine(120000, 96000, 30000, 0.9);
        var output = RunResampler(input, 2.0, 48000);
        var mono = Left(output, 1000);
        double rms = Math.Sqrt(mono.Select(v => (double)v * v).Average());
        double db = 20 * Math.Log10(rms / (0.9 / Math.Sqrt(2)));
        _out.WriteLine($"alias level {db:F1} dB");
        Assert.True(db < -100, $"alias {db:F1} dB");
    }

    [Theory]
    [InlineData(250)]
    [InlineData(-400)]
    public void DriftSource_TracksClockDriftWithoutGlitches(double ppm)
    {
        const int rate = 48000, packet = 480;
        var src = new DriftSource(rate, rate, targetLatencyMs: 40);
        var rnd = new Random(1);
        double producerPeriod = packet / (rate * (1 + ppm * 1e-6));
        double consumerPeriod = packet / (double)rate;
        double tp = 0, tc = 0;
        long phase = 0;
        var outAll = new List<float>();
        var buf = new float[packet * 2];
        double seconds = 120;
        while (tc < seconds)
        {
            // producer packets arrive with up to 4 ms jitter but their sample clock is steady
            double arrive = tp + rnd.NextDouble() * 0.004;
            if (arrive <= tc)
            {
                var p = new float[packet * 2];
                for (int i = 0; i < packet; i++)
                {
                    float v = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * phase / rate));
                    p[i * 2] = v; p[i * 2 + 1] = v; phase++;
                }
                src.Write(p, 2, tp);
                tp += producerPeriod;
                continue;
            }
            src.Read(buf, packet, tc);
            if (tc > 40) outAll.AddRange(Left(buf));
            tc += consumerPeriod;
        }
        _out.WriteLine($"ppm={src.CorrectionPpm:F1} buffered={src.BufferedMs:F1}ms underruns={src.Underruns} resyncs={src.Resyncs}");
        var rep = SineAnalyzer.Analyze(outAll.ToArray(), rate, 1000 * (1 + ppm * 1e-6));
        _out.WriteLine(rep.ToString());
        Assert.Equal(0, src.Underruns);
        Assert.Equal(0, rep.GlitchBlocks);
        Assert.Equal(0, rep.SilentBlocks);
        Assert.True(rep.MedianThdNDb < -110, $"THD+N {rep.MedianThdNDb:F1}");
        Assert.True(rep.WorstThdNDb < -100, $"worst THD+N {rep.WorstThdNDb:F1}");
        Assert.InRange(src.CorrectionPpm, ppm - 25, ppm + 25);
        Assert.InRange(src.BufferedMs, 25, 55);
    }

    /// <summary>
    /// Realistic model of the real thing: a device with a 22 ms buffer that drains continuously on its
    /// own clock, render callbacks every ~10 ms with +-3 ms scheduling jitter, producer packets with
    /// up to 4 ms delivery jitter. This is the case that exposed the padding issue on real Windows.
    /// </summary>
    [Theory]
    [InlineData(176, 2)]
    [InlineData(-320, 3)]
    [InlineData(40, 4)]
    public void DriftSource_RealisticDevice(double ppm, int seed)
    {
        const int rate = 48000, packet = 480, devBuf = 1056;
        var src = new DriftSource(rate, rate, 40);
        var rnd = new Random(seed);
        double pp = packet / (rate * (1 + ppm * 1e-6));
        double tp = 0, nextArr = rnd.NextDouble() * 0.004, tc = 0; long devWritten = 0, phase = 0;
        var outAll = new List<float>(); var p = new float[packet * 2]; var buf = new float[devBuf * 2];
        while (tc < 70)
        {
            if (tp + nextArr <= tc)
            {
                for (int i = 0; i < packet; i++) { float v = (float)(0.5 * Math.Sin(2 * Math.PI * 997 * phase++ / rate)); p[i * 2] = v; p[i * 2 + 1] = v; }
                src.Write(p, 2, tp); tp += pp; nextArr = rnd.NextDouble() * 0.004; continue;
            }
            int padding = (int)Math.Max(0, devWritten - tc * rate);
            int frames = devBuf - padding;
            if (frames > 0)
            {
                src.Read(buf, frames, tc, padding);
                if (tc > 12) for (int i = 0; i < frames; i++) outAll.Add(buf[i * 2]);
                devWritten += frames;
            }
            tc += 0.010 + (rnd.NextDouble() - 0.5) * 0.006;
        }
        var rep = SineAnalyzer.Analyze(outAll.ToArray(), rate, 997 * (1 + ppm * 1e-6));
        _out.WriteLine($"ppm={src.CorrectionPpm:F1} underruns={src.Underruns} {rep}");
        Assert.Equal(0, src.Underruns);
        Assert.Equal(0, rep.GlitchBlocks);
        Assert.Equal(0, rep.SilentBlocks);
        Assert.True(rep.MedianThdNDb < -120, $"median THD+N {rep.MedianThdNDb:F1}");
        Assert.True(rep.WorstThdNDb < -105, $"worst THD+N {rep.WorstThdNDb:F1}");
        Assert.InRange(src.CorrectionPpm, ppm - 15, ppm + 15);
    }

    [Fact]
    public void Limiter_IsBitTransparentBelowCeiling()
    {
        var lim = new Limiter(48000);
        var input = Sine(48000, 48000, 440, 0.8); // -1.9 dBFS, under the -1 dB ceiling
        var work = (float[])input.Clone();
        for (int o = 0; o < 48000; o += 480) lim.Process(work.AsSpan(o * 2, 960), 480);
        int d = lim.LatencyFrames;
        for (int i = 0; i < 48000 - d; i++)
        {
            Assert.Equal(input[i * 2], work[(i + d) * 2]);
            Assert.Equal(input[i * 2 + 1], work[(i + d) * 2 + 1]);
        }
    }

    [Fact]
    public void Limiter_NeverExceedsCeiling()
    {
        var lim = new Limiter(48000);
        var rnd = new Random(5);
        var input = new float[48000 * 2];
        for (int i = 0; i < input.Length; i++) input[i] = (float)((rnd.NextDouble() * 2 - 1) * (i % 9000 < 3000 ? 3.0 : 0.3));
        for (int o = 0; o < 48000; o += 480) lim.Process(input.AsSpan(o * 2, 960), 480);
        float max = input.Max(Math.Abs);
        Assert.True(max <= lim.Ceiling * 1.000001f, $"peak {max}");
    }

    [Fact]
    public void RingBuffer_WrapsCorrectly()
    {
        var rb = new StereoRingBuffer(100);
        var tmp = new float[200];
        int counter = 0, expect = 0;
        for (int round = 0; round < 50; round++)
        {
            var w = new float[70 * 2];
            for (int i = 0; i < w.Length; i++) w[i] = counter++;
            Assert.Equal(70, rb.Write(w));
            Assert.Equal(70, rb.Read(tmp.AsSpan(0, 140)));
            for (int i = 0; i < 140; i++) Assert.Equal(expect++, tmp[i]);
        }
    }
}
