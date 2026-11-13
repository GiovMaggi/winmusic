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
    //
    // 1. MEASURE (first 6 s after audio starts): the speed is left alone and we fit a straight line
    //    through the buffer level. Its slope IS the clock drift between the two devices, measured
    //    precisely (a few ppm), so we can set the correction once, smoothly, without hunting.
    // 2. TRACK: a very gentle PI loop (2 s averages, tiny gains) keeps the buffer centred. The
    //    correction is always ramped, so the playback speed never jumps; it changes by well under
    //    1 ppm per second, which keeps the pitch modulation far below audibility (and below -110 dB).
    private bool _running;
    private bool _measuring;
    private double _runTime;
    private double _sn, _st, _sy, _stt, _sty;       // regression sums during MEASURE
    private double _fillSum;
    private int _fillCount;
    private double _periodElapsed;
    private double _setpoint;                       // buffer level (frames) we hold during TRACK
    private double _integral;
    private double _adjust;                         // currently applied correction (fraction)
    private double _adjustFrom, _adjustTo, _rampPos = 1, _rampSeconds = 2;

    private const double MeasureDelay = 1.0;        // let the arrival-time filter fill first
    private const double BinSeconds = 0.25;         // step detector resolution
    private const double StepThreshold = 0.004;     // a 4 ms change between 250 ms averages is a step, not drift
    private double _measureT0;
    private double _binSum, _binElapsed, _prevBin = double.NaN;
    private int _binCount;
    private double _silentSeconds;
    private double _startFrames;                    // level to refill to before playing (normally the target)
    // While paused, keep this much extra in reserve: when an app resumes, Windows drops ~15 ms of
    // its stream timeline, and the reserve absorbs that so the restarted music never underruns.
    private const double ResumeHeadroom = 0.015;
    private const double MeasureSeconds = 6.0;
    private const double TrackPeriod = 2.0;
    private const double KpTrack = 0.01, KiTrack = 0.00005;
    private const double JumpThreshold = 0.005;     // a 2 s average this far off the setpoint is a jump, not drift
    private const double MaxAdjust = 0.005;         // +-0.5 % (real drift is usually < 0.03 %)


    /// <param name="outputBufferFrames">
    /// Size of the output device buffer (frames at the output rate). Audio we've handed to the device
    /// but it hasn't played yet is counted as buffered, using the padding passed to <see cref="Read"/>.
    /// </param>
    public DriftSource(int inRate, int outRate, double targetLatencyMs = 40, double ringSeconds = 2.0, int outputBufferFrames = 0)
    {
        _inRate = inRate;
        _outRate = outRate;
        _nominalStep = (double)inRate / outRate;
        _ring = new StereoRingBuffer((int)(inRate * ringSeconds));
        _resampler = new SincResampler(_nominalStep);
        _targetFrames = targetLatencyMs / 1000.0 * inRate;
        _maxFrames = _targetFrames + 0.12 * inRate;
        _startFrames = _targetFrames;
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
    public long Jumps { get; private set; }
    /// <summary>Optional diagnostics (called on the render thread, rarely).</summary>
    public Action<string>? Trace;

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

    // Packet arrival times only ever suffer *late* jitter (thread wake-up delays), never early.
    // So the earliest arrivals over the last second describe the true stream timeline: we keep a
    // sliding minimum of (arrival - stream position) and build the timeline from that.
    private readonly double[] _offT = new double[1024], _offV = new double[1024];
    private int _offHead, _offCount;
    private double _lastArrival = double.NaN;

    private void Publish(int written, int frames, double firstFrameTime)
    {
        if (written < frames) Overflows++;
        _totalWritten += written;
        double end = firstFrameTime + frames / _inRate;
        if (!double.IsNaN(_lastArrival) && end - _lastArrival > 0.1) _offCount = 0; // stream paused: start fresh
        _lastArrival = end;
        double o = end - _totalWritten / _inRate;
        int n = _offT.Length;
        while (_offCount > 0 && _offV[(_offHead + _offCount - 1) % n] >= o) _offCount--;
        if (_offCount == n) { _offHead = (_offHead + 1) % n; _offCount--; }
        int tail = (_offHead + _offCount) % n;
        _offT[tail] = end; _offV[tail] = o; _offCount++;
        while (_offCount > 1 && _offT[_offHead] < end - 1.0) { _offHead = (_offHead + 1) % n; _offCount--; }
        double minOffset = _offV[_offHead];
        Volatile.Write(ref _stamp, new Stamp(_totalWritten, _totalWritten / _inRate + minOffset));
    }

    /// <summary>Producer side: write silence (e.g. AUDCLNT_BUFFERFLAGS_SILENT packets).</summary>
    public void WriteSilence(int frames, double firstFrameTime)
    {
        if (_convert.Length < frames * 2) _convert = new float[frames * 2];
        var c = _convert.AsSpan(0, frames * 2);
        c.Clear();
        Publish(_ring.Write(c), frames, firstFrameTime);
    }

    private void StartMeasure()
    {
        // keep _adjust: the drift between two devices doesn't change when music pauses, we just refine it
        _measuring = true;
        _runTime = 0;
        _measureT0 = 0;
        _prevBin = double.NaN; _binSum = 0; _binCount = 0; _binElapsed = 0;
        _sn = _st = _sy = _stt = _sty = 0;
        _adjustFrom = _adjustTo = _adjust; _rampPos = 1;
    }

    private void Ramp(double to, double seconds)
    {
        _adjustFrom = _adjust; _adjustTo = to; _rampPos = 0; _rampSeconds = seconds;
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
    /// <paramref name="queuedOutputFrames"/> is how much audio is still waiting in the output device buffer (WASAPI padding).
    /// </summary>
    public void Read(Span<float> dst, int outFrames, double now, int queuedOutputFrames = 0)
    {
        double fill = _ring.AvailableFrames + _resampler.BufferedAheadFrames;
        double dt = outFrames / _outRate;

        if (!_running)
        {
            if (fill >= _startFrames)
            {
                _startFrames = _targetFrames;
                _running = true;
                StartMeasure();
                Trace?.Invoke($"start: level {fill / _inRate * 1000:F1} ms, keeping {_adjust * 1e6:F1} ppm");
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
            Resyncs++;
            Trace?.Invoke($"resync: level was {fill / _inRate * 1000:F1} ms");
            StartMeasure();
        }

        double level = EstimateFill(now) + queuedOutputFrames * _nominalStep;
        _runTime += dt;

        // Step detector: compare consecutive 250 ms averages of the buffer level. Clock drift moves
        // the level by microseconds per 250 ms; a jump of several ms is a stream event (app paused,
        // resumed, burst) and must never be mistaken for drift.
        _binSum += level; _binCount++; _binElapsed += dt;
        bool stepDetected = false;
        if (_binElapsed >= BinSeconds)
        {
            double bin = _binSum / _binCount;
            if (!double.IsNaN(_prevBin) && Math.Abs(bin - _prevBin) > StepThreshold * _inRate) stepDetected = true;
            _prevBin = bin;
            _binSum = 0; _binCount = 0; _binElapsed = 0;
        }
        if (stepDetected)
        {
            Jumps++;
            Trace?.Invoke($"step: level moved suddenly to {_prevBin / _inRate * 1000:F1} ms → {(_measuring ? "restart measurement" : "re-measure")}");
            if (_measuring) { _measureT0 = _runTime; _sn = _st = _sy = _stt = _sty = 0; }
            else { StartMeasure(); _measureT0 = _runTime; }
        }

        if (_measuring)
        {
            double t = _runTime - _measureT0;
            if (t >= MeasureDelay) { _sn++; _st += t; _sy += level; _stt += t * t; _sty += t * level; }
            if (t >= MeasureDelay + MeasureSeconds)
            {
                double den = _sn * _stt - _st * _st;
                double slope = den > 0 ? (_sn * _sty - _st * _sy) / den : 0;   // frames per second
                double newAdjust = Math.Clamp(_adjust + slope / _inRate, -MaxAdjust, MaxAdjust);
                _setpoint = Math.Max(_sy / _sn + slope * (t - _st / _sn), _targetFrames); // level now
                _integral = newAdjust / KiTrack;
                Trace?.Invoke($"measured: slope {slope / _inRate * 1e6:F1} ppm over {_sn} samples → correction {_adjust * 1e6:F1} → {newAdjust * 1e6:F1} ppm, setpoint {_setpoint / _inRate * 1000:F1} ms");
                Ramp(newAdjust, 3.0);
                _measuring = false;
                _fillSum = 0; _fillCount = 0; _periodElapsed = 0;
            }
        }
        else
        {
            _fillSum += level; _fillCount++;
            _periodElapsed += dt;
            if (_periodElapsed >= TrackPeriod)
            {
                double errSec = (_fillSum / _fillCount - _setpoint) / _inRate;
                if (Math.Abs(errSec) > JumpThreshold)
                {
                    // The buffer level jumped (e.g. Windows delivered a burst when the app resumed).
                    // That's not clock drift: don't change the speed to "fix" it, just re-measure and
                    // hold the new level. (Too high/low is still handled by resync/rebuffer.)
                    Jumps++;
                    Trace?.Invoke($"jump: level {_fillSum / _fillCount / _inRate * 1000:F1} ms vs setpoint {_setpoint / _inRate * 1000:F1} ms → re-measure");
                    StartMeasure();
                    _fillSum = 0; _fillCount = 0; _periodElapsed = 0;
                    goto applyRamp;
                }
                double kp = KpTrack;
                _integral += errSec * _periodElapsed;
                double target = kp * errSec + KiTrack * _integral;
                if (Math.Abs(target) > MaxAdjust)
                {
                    target = Math.Clamp(target, -MaxAdjust, MaxAdjust);
                    _integral -= errSec * _periodElapsed; // anti-windup
                }
                Ramp(target, TrackPeriod);
                Trace?.Invoke($"track: err {errSec * 1000:F2} ms → {target * 1e6:F1} ppm");
                _fillSum = 0; _fillCount = 0; _periodElapsed = 0;
            }
        }
        applyRamp:
        if (_rampPos < 1)
        {
            _rampPos = Math.Min(1, _rampPos + dt / _rampSeconds);
            _adjust = _adjustFrom + (_adjustTo - _adjustFrom) * _rampPos;
        }
        double step = _nominalStep * (1.0 + _adjust);

        int need = _resampler.InputFramesNeeded(outFrames, step);
        if (need > _ring.AvailableFrames)
        {
            // Source stopped delivering (app paused or closed). Go quiet and rebuffer.
            Underruns++;
            Trace?.Invoke($"underrun: needed {need}, ring has {_ring.AvailableFrames}");
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
        float peak = 0;
        for (int i = 0; i < outFrames * 2; i++) { float a = Math.Abs(dst[i]); if (a > peak) peak = a; }
        _silentSeconds = peak < 1e-4f ? _silentSeconds + dt : 0;

        if (_silentSeconds >= 0.5)
        {
            double ringLevel = _ring.AvailableFrames + _resampler.BufferedAheadFrames;
            double desired = _targetFrames + ResumeHeadroom * _inRate;
            if (Math.Abs(ringLevel - desired) > 0.003 * _inRate)
            {
                if (ringLevel > desired) _totalRead += _ring.Skip((int)(ringLevel - desired));
                else { _running = false; _startFrames = desired; }   // refill silently
                Trace?.Invoke($"silence: buffer re-centred from {ringLevel / _inRate * 1000:F1} to {desired / _inRate * 1000:F1} ms");
                _silentSeconds = 0;   // don't re-check every callback
            }
        }
    }
}
