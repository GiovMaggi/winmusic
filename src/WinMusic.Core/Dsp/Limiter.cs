namespace WinMusic.Core.Dsp;

/// <summary>
/// Transparent look-ahead peak limiter (stereo-linked).
/// While the signal stays under the ceiling the gain is exactly 1.0, so audio passes through
/// bit-for-bit (just delayed by the look-ahead). It only acts when music + voice would clip,
/// and then it fades gain smoothly instead of distorting.
/// </summary>
public sealed class Limiter
{
    private readonly int _look;            // look-ahead in frames
    private readonly float _ceiling;
    private readonly double _releaseCoef;

    private readonly float[] _delay;       // stereo delay line
    private int _delayPos;

    // sliding minimum of required gain (monotonic deque over a ring of size _look)
    private readonly float[] _req;
    private readonly long[] _dqIdx;
    private readonly float[] _dqVal;
    private int _dqHead, _dqCount;
    private long _t;

    private double _env = 1.0;

    // moving average of envelope
    private readonly double[] _box;
    private int _boxPos;
    private double _boxSum;
    private int _nonUnity;                 // how many entries in _box are != 1

    public Limiter(int sampleRate, double ceilingDb = -1.0, double lookaheadMs = 2.0, double releaseMs = 120.0)
    {
        _look = Math.Max(2, (int)Math.Round(sampleRate * lookaheadMs / 1000.0));
        _ceiling = (float)Math.Pow(10, ceilingDb / 20.0);
        _releaseCoef = 1.0 - Math.Exp(-1.0 / (sampleRate * releaseMs / 1000.0));
        _delay = new float[(_look - 1) * 2 + 2];
        _req = new float[_look];
        _dqIdx = new long[_look + 1];
        _dqVal = new float[_look + 1];
        _box = new double[_look];
        Array.Fill(_box, 1.0);
        _boxSum = _look;
    }

    public float Ceiling => _ceiling;
    public int LatencyFrames => _look - 1;

    /// <summary>Lowest gain applied during the last Process call (1.0 = untouched).</summary>
    public float LastMinGain { get; private set; } = 1f;

    public void Process(Span<float> stereo, int frames)
    {
        float minGain = 1f;
        int delayFrames = _look - 1;
        for (int i = 0; i < frames; i++)
        {
            float l = stereo[i * 2], r = stereo[i * 2 + 1];
            float peak = Math.Max(Math.Abs(l), Math.Abs(r));
            float req = peak > _ceiling ? _ceiling / peak : 1f;

            // push into sliding-min deque
            while (_dqCount > 0 && _dqVal[(_dqHead + _dqCount - 1) % _dqVal.Length] >= req) _dqCount--;
            int tail = (_dqHead + _dqCount) % _dqVal.Length;
            _dqVal[tail] = req; _dqIdx[tail] = _t; _dqCount++;
            while (_dqIdx[_dqHead] <= _t - _look) { _dqHead = (_dqHead + 1) % _dqVal.Length; _dqCount--; }
            double m = _dqVal[_dqHead];
            _t++;

            // instant attack to the held minimum, exponential release
            if (m < _env) _env = m;
            else
            {
                _env += (m - _env) * _releaseCoef;
                if (_env > 0.9999999) _env = 1.0;
            }

            // box filter smooths the attack so it completes exactly within the look-ahead
            double old = _box[_boxPos];
            if (old != 1.0) _nonUnity--;
            if (_env != 1.0) _nonUnity++;
            _boxSum += _env - old;
            _box[_boxPos] = _env;
            if (++_boxPos == _look)
            {
                _boxPos = 0;
                double s = 0; for (int k = 0; k < _look; k++) s += _box[k];
                _boxSum = s; // kill rounding drift
            }
            float g = _nonUnity == 0 ? 1f : (float)Math.Min(1.0, _boxSum / _look);
            if (g < minGain) minGain = g;

            // delay line
            int dp = _delayPos * 2;
            float dl = _delay[dp], dr = _delay[dp + 1];
            _delay[dp] = l; _delay[dp + 1] = r;
            if (++_delayPos >= delayFrames) _delayPos = 0;

            if (g == 1f) { stereo[i * 2] = dl; stereo[i * 2 + 1] = dr; }
            else { stereo[i * 2] = dl * g; stereo[i * 2 + 1] = dr * g; }
        }
        LastMinGain = minGain;
    }
}

/// <summary>Click-free gain changes. Exactly 1.0 when at unity so nothing is altered.</summary>
public sealed class GainRamp
{
    private float _current;
    public GainRamp(float initial = 1f) { _current = initial; }
    public float Current => _current;

    public void Apply(Span<float> stereo, int frames, float target)
    {
        if (_current == target)
        {
            if (target == 1f) return;
            if (target == 0f) { stereo.Slice(0, frames * 2).Clear(); return; }
            for (int i = 0; i < frames * 2; i++) stereo[i] *= target;
            return;
        }
        float start = _current;
        float inc = (target - start) / frames;
        for (int i = 0; i < frames; i++)
        {
            float g = start + inc * (i + 1);
            stereo[i * 2] *= g; stereo[i * 2 + 1] *= g;
        }
        _current = target;
    }

    public static float DbToGain(double db) => db <= -60 ? 0f : (float)Math.Pow(10, db / 20.0);
}
