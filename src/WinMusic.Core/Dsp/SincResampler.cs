namespace WinMusic.Core.Dsp;

/// <summary>
/// High quality streaming stereo resampler using a Kaiser-windowed sinc kernel
/// (160 taps at unity ratio, ~118 dB stopband) with a finely interpolated coefficient table.
/// Supports a continuously variable ratio, which is what lets us track clock drift between
/// two audio devices without ever dropping or duplicating samples.
/// </summary>
public sealed class SincResampler
{
    private const int Phases = 2048;          // table resolution per input sample
    private readonly int _half;               // kernel half width (in normalised units)
    private readonly float[] _table;          // kernel from u = 0 .. half
    private readonly double _scale;           // <= 1 when downsampling (widens kernel)
    private readonly int _taps;               // taps each side, in input samples
    private readonly double[] _coefs;

    private float[] _buf;                     // interleaved stereo input history
    private int _bufFrames;
    private double _pos;                      // fractional read position into _buf (frames)

    public SincResampler(double nominalStep, int halfTaps = 80, double cutoff = 0.95, double kaiserBeta = 12.0)
    {
        if (nominalStep <= 0) throw new ArgumentOutOfRangeException(nameof(nominalStep));
        _half = halfTaps;
        _scale = Math.Min(1.0, 1.0 / nominalStep);
        _taps = (int)Math.Ceiling(_half / _scale);
        _coefs = new double[_taps * 2];

        _table = new float[_half * Phases + 2];
        double fc = 0.5 * cutoff; // cycles per sample, relative to the (scaled) input rate
        double i0b = BesselI0(kaiserBeta);
        for (int i = 0; i < _table.Length; i++)
        {
            double u = (double)i / Phases;
            if (u >= _half) { _table[i] = 0f; continue; }
            double x = 2.0 * fc * u;
            double sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double r = u / _half;
            double win = BesselI0(kaiserBeta * Math.Sqrt(1.0 - r * r)) / i0b;
            _table[i] = (float)(2.0 * fc * sinc * win);
        }

        _buf = new float[Math.Max(4096, _taps * 8) * 2];
        Reset();
    }

    /// <summary>Latency introduced by the filter, in input frames.</summary>
    public int LatencyFrames => _taps;

    /// <summary>Input frames currently held ahead of the read position (counts towards buffering).</summary>
    public double BufferedAheadFrames => Math.Max(0, _bufFrames - _pos);

    public void Reset()
    {
        Array.Clear(_buf);
        _bufFrames = _taps;    // leading silence so the first sample is centred
        _pos = _taps;
    }

    /// <summary>How many more input frames must be supplied before <see cref="Produce"/> can make outFrames.</summary>
    public int InputFramesNeeded(int outFrames, double step)
    {
        double lastPos = _pos + (outFrames - 1) * step;
        long needBuf = (long)Math.Floor(lastPos) + _taps + 1;
        return (int)Math.Max(0, needBuf - _bufFrames);
    }

    /// <summary>Returns a writable span for <paramref name="frames"/> input frames; call <see cref="CommitInput"/> after filling.</summary>
    public Span<float> GetInputSpan(int frames)
    {
        int needed = (_bufFrames + frames) * 2;
        if (needed > _buf.Length)
        {
            var nb = new float[Math.Max(needed, _buf.Length * 2)];
            Array.Copy(_buf, nb, _bufFrames * 2);
            _buf = nb;
        }
        return _buf.AsSpan(_bufFrames * 2, frames * 2);
    }

    public void CommitInput(int frames) => _bufFrames += frames;

    /// <summary>Produces outFrames of output. Caller must have supplied <see cref="InputFramesNeeded"/> frames.</summary>
    public void Produce(Span<float> dst, int outFrames, double step)
    {
        var buf = _buf;
        var tab = _table;
        var coefs = _coefs;
        int taps = _taps;
        double scale = _scale;
        double phScale = Phases * scale;
        int tabMax = _half * Phases;

        for (int j = 0; j < outFrames; j++)
        {
            int n = (int)Math.Floor(_pos);
            double frac = _pos - n;

            // Coefficients for input samples n-taps+1 .. n+taps
            for (int k = -taps + 1, c = 0; k <= taps; k++, c++)
            {
                double t = k - frac;
                double idx = Math.Abs(t) * phScale;
                int i = (int)idx;
                if (i >= tabMax) { coefs[c] = 0; continue; }
                double f = idx - i;
                coefs[c] = (tab[i] + (tab[i + 1] - tab[i]) * f) * scale;
            }

            double l = 0, r = 0;
            int baseIdx = (n - taps + 1) * 2;
            for (int c = 0; c < coefs.Length; c++)
            {
                double w = coefs[c];
                l += w * buf[baseIdx + c * 2];
                r += w * buf[baseIdx + c * 2 + 1];
            }
            dst[j * 2] = (float)l;
            dst[j * 2 + 1] = (float)r;
            _pos += step;
        }

        // Discard history we no longer need.
        int drop = (int)Math.Floor(_pos) - taps + 1;
        if (drop > 0)
        {
            int keep = _bufFrames - drop;
            if (keep > 0) Array.Copy(buf, drop * 2, buf, 0, keep * 2);
            _bufFrames = Math.Max(0, keep);
            _pos -= drop;
        }
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 64; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}
