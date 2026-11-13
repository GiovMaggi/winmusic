namespace WinMusic.Core.Analysis;

/// <summary>Result of fitting a pure sine to a block of audio.</summary>
public readonly record struct SineFit(double Frequency, double Amplitude, double ThdNDb, double ResidualRms);

/// <summary>Summary over a long recording split into blocks.</summary>
public sealed record SineReport(
    int Blocks, double Frequency, double Amplitude,
    double MedianThdNDb, double WorstThdNDb, int GlitchBlocks, int SilentBlocks, string GlitchAt = "", int TotalBlocks = 0)
{
    public override string ToString() =>
        $"blocks={Blocks} freq={Frequency:F3}Hz amp={Amplitude:F5} ({20 * Math.Log10(Math.Max(Amplitude, 1e-12)):F2} dBFS) " +
        $"THD+N median={MedianThdNDb:F1} dB worst={WorstThdNDb:F1} dB glitchBlocks={GlitchBlocks} silentBlocks={SilentBlocks}" +
        (GlitchAt.Length > 0 ? $" glitchAt=[{GlitchAt}] of {TotalBlocks} blocks" : "");
}

/// <summary>
/// Measures audio quality by least-squares fitting a sine (IEEE-1057 style 4 parameter fit)
/// and reporting everything that is not that sine as THD+N. Used by the unit tests and by the
/// on-device self test in CI.
/// </summary>
public static class SineAnalyzer
{
    public static SineFit Fit(ReadOnlySpan<float> x, double sampleRate, double freqGuess)
    {
        // golden section search on frequency, solving the linear 3-param fit at each step
        double lo = freqGuess * 0.995, hi = freqGuess * 1.005;
        const double gr = 0.6180339887498949;
        double c = hi - gr * (hi - lo), d = lo + gr * (hi - lo);
        double fc = Residual(x, sampleRate, c, out _), fd = Residual(x, sampleRate, d, out _);
        for (int it = 0; it < 60; it++)
        {
            if (fc < fd) { hi = d; d = c; fd = fc; c = hi - gr * (hi - lo); fc = Residual(x, sampleRate, c, out _); }
            else { lo = c; c = d; fc = fd; d = lo + gr * (hi - lo); fd = Residual(x, sampleRate, d, out _); }
        }
        double f = (lo + hi) / 2;
        double res = Residual(x, sampleRate, f, out double amp);
        double thdn = 20 * Math.Log10(Math.Max(res, 1e-15) / Math.Max(amp / Math.Sqrt(2), 1e-15));
        return new SineFit(f, amp, thdn, res);
    }

    private static double Residual(ReadOnlySpan<float> x, double fs, double f, out double amp)
    {
        int n = x.Length;
        double w = 2 * Math.PI * f / fs;
        // normal equations for [cos, sin, 1]
        double cc = 0, ss = 0, cs = 0, c1 = 0, s1 = 0, yc = 0, ys = 0, y1 = 0;
        for (int i = 0; i < n; i++)
        {
            double co = Math.Cos(w * i), si = Math.Sin(w * i), y = x[i];
            cc += co * co; ss += si * si; cs += co * si; c1 += co; s1 += si;
            yc += y * co; ys += y * si; y1 += y;
        }
        // solve 3x3
        double[,] m = { { cc, cs, c1 }, { cs, ss, s1 }, { c1, s1, n } };
        double[] b = { yc, ys, y1 };
        var p = Solve3(m, b);
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double e = x[i] - (p[0] * Math.Cos(w * i) + p[1] * Math.Sin(w * i) + p[2]);
            sum += e * e;
        }
        amp = Math.Sqrt(p[0] * p[0] + p[1] * p[1]);
        return Math.Sqrt(sum / n);
    }

    private static double[] Solve3(double[,] a, double[] b)
    {
        double det(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) -
            m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
            m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double D = det(a);
        var r = new double[3];
        for (int col = 0; col < 3; col++)
        {
            var t = (double[,])a.Clone();
            for (int row = 0; row < 3; row++) t[row, col] = b[row];
            r[col] = det(t) / D;
        }
        return r;
    }

    /// <summary>
    /// Splits a mono signal into blocks and fits each one. Blocks that deviate badly from the
    /// typical result (a click, dropout or repeated chunk) are counted as glitches.
    /// </summary>
    public static SineReport Analyze(ReadOnlySpan<float> mono, double sampleRate, double freqGuess, int blockSize = 4800, double glitchThresholdDb = -60)
    {
        var fits = new List<SineFit>();
        var glitchAt = new List<string>();
        int silent = 0, index = 0;
        for (int off = 0; off + blockSize <= mono.Length; off += blockSize, index++)
        {
            var blk = mono.Slice(off, blockSize);
            double peak = 0; foreach (var v in blk) peak = Math.Max(peak, Math.Abs(v));
            if (peak < 1e-4) { silent++; continue; }
            var fit = Fit(blk, sampleRate, freqGuess);
            if (fit.ThdNDb > glitchThresholdDb) glitchAt.Add($"{index}:{fit.ThdNDb:F0}dB");
            fits.Add(fit);
        }
        if (fits.Count == 0) return new SineReport(0, 0, 0, 0, 0, 0, silent);
        var thd = fits.Select(f => f.ThdNDb).OrderBy(v => v).ToArray();
        double median = thd[thd.Length / 2];
        int glitches = fits.Count(f => f.ThdNDb > glitchThresholdDb);
        return new SineReport(fits.Count,
            fits.Select(f => f.Frequency).OrderBy(v => v).ElementAt(fits.Count / 2),
            fits.Select(f => f.Amplitude).OrderBy(v => v).ElementAt(fits.Count / 2),
            median, thd[^1], glitches, silent, string.Join(" ", glitchAt), index);
    }
}
