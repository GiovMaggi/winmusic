namespace WinMusic.Core.Dsp;

/// <summary>
/// Bridges an input stream running on one clock (e.g. Spotify's playback, or a microphone)
/// into an output stream running on another clock (the virtual cable).
///
/// Every audio device has its own crystal, so two "48 kHz" devices are never exactly the same
/// speed. Cheap mixers fix this by dropping or repeating samples (clicks). We instead measure
/// how full our buffer is and continuously nudge a high quality sinc resampler by a few
/// parts-per-million, so the audio stays continuous and pitch changes are far below audibility.
/// </summary>
public sealed class DriftSource
{
    private readonly StereoRingBuffer _ring;
    private readonly SincResampler _resampler;
    private readonly double _inRate;
    private readonly double _outRate;
    private readonly double _nominalStep;
    private readonly double _targetFrames;
    private readonly double _maxFrames;
    private float[] _convert = new float[8192];

    // Producer publishes "frame index W ended at time T" so the consumer can estimate the exact
    // buffer level at any instant (removes packet-size quantisation and delivery jitter).
    private sealed record Stamp(long EndIndex, double EndTime);
    private Stamp? _stamp;
    private long _totalWritten;   // producer only
    private long _totalRead;      // consumer only

    // controller state (render thread only)
    private bool _running;
    private double _fillSum;
    private int _fillCount;
    private double _periodElapsed;
    private double _runTime;
    private double _integral;
    private double _adjust;        // currently applied correction (fraction)
    private double _adjustFrom, _adjustTo, _rampPos;

    // The buffer level is averaged over 1 s (removes packet saw-tooth and scheduling jitter),
    // then a PI controller updates the target correction once per second and the applied
    // correction is ramped linearly between updates so the speed never jumps.
    private const double UpdatePeriod = 1.0;
    private const double AcquireSeconds = 25.0;
    private const double KpAcquire = 0.15, KiAcquire = 0.008;   // fast lock-in after start
    private const double KpTrack = 0.04, KiTrack = 0.0005;      // very smooth once locked
    private const double MaxAdjust = 0.005; // +-0.5 % (real drift is usually < 0.03 %)

    public DriftSource(int inRate, int outRate, double targetLatencyMs = 40, double ringSeconds = 2.0)
    {
        _inRate = inRate;
        _outRate = outRate;
        _nominalStep = (double)inRate / outRate;
        _ring = new StereoRingBuffer((int)(inRate * ringSeconds));
        _resampler = new SincResampler(_nominalStep);
        _targetFrames = targetLatencyMs / 1000.0 * inRate;
        _maxFrames = _targetFrames + 0.12 * inRate;
    }

    public int InputRate => (int)_inRate;
    public bool IsPlaying => _running;
    /// <summary>Current speed correction in parts per million.</summary>
    public double CorrectionPpm => _adjust * 1e6;
    /// <summary>Current buffered latency in milliseconds.</summary>
    public double BufferedMs => (_ring.AvailableFrames + _resampler.BufferedAheadFrames) / _inRate * 1000.0;
    public long Underruns { get; private set; }
    public long Overflows { get; private set; }
    public long Resyncs { get; private set; }

    /// <summary>
    /// Producer side (capture thread). Accepts interleaved float with any channel count.
    /// <paramref name="firstFrameTime"/> is when the first frame was captured, in seconds on the
    /// same clock passed to <see cref="Read"/> (QPC / Stopwatch).
    /// </summary>
    public void Write(ReadOnlySpan<float> interleaved, int channels, double firstFrameTime)
    {
        int frames = interleaved.Length / channels;
        if (channels == 2)
        {
            Publish(_ring.Write(interleaved.Slice(0, frames * 2)), frames, firstFrameTime);
            return;
        }
        if (_convert.Length < frames * 2) _convert = new float[frames * 2];
        var c = _convert.AsSpan(0, frames * 2);
        if (channels == 1)
        {
            for (int i = 0; i < frames; i++) { float v = interleaved[i]; c[i * 2] = v; c[i * 2 + 1] = v; }
        }
        else
        {
            for (int i = 0; i < frames; i++) { c[i * 2] = interleaved[i * channels]; c[i * 2 + 1] = interleaved[i * channels + 1]; }
        }
        Publish(_ring.Write(c), frames, firstFrameTime);
    }

    private void Publish(int written, int frames, double firstFrameTime)
    {
        if (written < frames) Overflows++;
        _totalWritten += written;
        Volatile.Write(ref _stamp, new Stamp(_totalWritten, firstFrameTime + frames / _inRate));
    }

    /// <summary>Producer side: write silence (e.g. AUDCLNT_BUFFERFLAGS_SILENT packets).</summary>
    public void WriteSilence(int frames, double firstFrameTime)
    {
        if (_convert.Length < frames * 2) _convert = new float[frames * 2];
        var c = _convert.AsSpan(0, frames * 2);
        c.Clear();
        Publish(_ring.Write(c), frames, firstFrameTime);
    }

    /// <summary>Buffer level estimate in input frames at time <paramref name="now"/>.</summary>
    private double EstimateFill(double now)
    {
        var st = Volatile.Read(ref _stamp);
        double consumed = _totalRead - _resampler.BufferedAheadFrames;
        if (st == null) return _ring.AvailableFrames + _resampler.BufferedAheadFrames;
        double since = Math.Clamp(now - st.EndTime, -0.05, 0.05);
        return st.EndIndex + since * _inRate - consumed;
    }

    /// <summary>
    /// Consumer side (render thread). Always fills dst with exactly outFrames stereo frames.
    /// <paramref name="now"/> is the current time in seconds (same clock as Write).
    /// </summary>
    public void Read(Span<float> dst, int outFrames, double now)
    {
        double fill = _ring.AvailableFrames + _resampler.BufferedAheadFrames;
        double dt = outFrames / _outRate;

        if (!_running)
        {
            if (fill >= _targetFrames)
            {
                _running = true;
                _fillSum = 0; _fillCount = 0; _periodElapsed = 0; _runTime = 0;
                // keep _integral/_adjust: the drift between two devices does not change when music pauses
                _adjustFrom = _adjustTo = _adjust; _rampPos = 1;
            }
            else
            {
                dst.Slice(0, outFrames * 2).Clear();
                return;
            }
        }

        // If something stalled and a big backlog built up, jump forward to keep latency low.
        if (fill > _maxFrames)
        {
            _totalRead += _ring.Skip((int)(fill - _targetFrames));
            _fillSum = 0; _fillCount = 0; _periodElapsed = 0;
            Resyncs++;
        }

        _fillSum += EstimateFill(now); _fillCount++;
        _periodElapsed += dt; _runTime += dt;
        if (_periodElapsed >= UpdatePeriod)
        {
            double errSec = (_fillSum / _fillCount - _targetFrames) / _inRate;
            bool acquire = _runTime < AcquireSeconds;
            double kp = acquire ? KpAcquire : KpTrack, ki = acquire ? KiAcquire : KiTrack;
            // convert integral between gain sets so the output doesn't jump when switching
            _integral += errSec * _periodElapsed;
            double target = kp * errSec + ki * _integral;
            if (target > MaxAdjust || target < -MaxAdjust)
            {
                target = Math.Clamp(target, -MaxAdjust, MaxAdjust);
                _integral -= errSec * _periodElapsed; // anti-windup
            }
            _adjustFrom = _adjust; _adjustTo = target; _rampPos = 0;
            _fillSum = 0; _fillCount = 0; _periodElapsed = 0;
            if (acquire && _runTime + UpdatePeriod >= AcquireSeconds)
                _integral = (target - KpTrack * errSec) / KiTrack; // bumpless switch to tracking gains
        }
        if (_rampPos < 1)
        {
            _rampPos = Math.Min(1, _rampPos + dt / UpdatePeriod);
            _adjust = _adjustFrom + (_adjustTo - _adjustFrom) * _rampPos;
        }
        double step = _nominalStep * (1.0 + _adjust);

        int need = _resampler.InputFramesNeeded(outFrames, step);
        if (need > _ring.AvailableFrames)
        {
            // Source stopped delivering (app paused or closed). Go quiet and rebuffer.
            Underruns++;
            _running = false;
            _resampler.Reset();
            _totalRead += _ring.Skip(_ring.AvailableFrames);
            dst.Slice(0, outFrames * 2).Clear();
            return;
        }
        if (need > 0)
        {
            var span = _resampler.GetInputSpan(need);
            _totalRead += _ring.Read(span);
            _resampler.CommitInput(need);
        }
        _resampler.Produce(dst, outFrames, step);
    }
}
