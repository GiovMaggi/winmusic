using System.Threading;

namespace WinMusic.Core.Dsp;

/// <summary>
/// Lock-free single-producer / single-consumer ring buffer of interleaved stereo float frames.
/// The capture thread writes, the render thread reads.
/// </summary>
public sealed class StereoRingBuffer
{
    private readonly float[] _data;
    private readonly int _capacityFrames;
    private long _writeFrames; // total frames ever written (owned by producer)
    private long _readFrames;  // total frames ever read (owned by consumer)

    public StereoRingBuffer(int capacityFrames)
    {
        _capacityFrames = capacityFrames;
        _data = new float[capacityFrames * 2];
    }

    public int CapacityFrames => _capacityFrames;

    public int AvailableFrames => (int)(Volatile.Read(ref _writeFrames) - Volatile.Read(ref _readFrames));

    public int FreeFrames => _capacityFrames - AvailableFrames;

    /// <summary>Writes up to frames.Length/2 frames. Returns frames actually written (drops what does not fit).</summary>
    public int Write(ReadOnlySpan<float> stereo)
    {
        int frames = stereo.Length / 2;
        long w = _writeFrames;
        long r = Volatile.Read(ref _readFrames);
        int free = _capacityFrames - (int)(w - r);
        if (frames > free) frames = free;
        if (frames <= 0) return 0;

        int start = (int)(w % _capacityFrames);
        int first = Math.Min(frames, _capacityFrames - start);
        stereo.Slice(0, first * 2).CopyTo(_data.AsSpan(start * 2));
        if (first < frames)
            stereo.Slice(first * 2, (frames - first) * 2).CopyTo(_data.AsSpan(0));

        Volatile.Write(ref _writeFrames, w + frames);
        return frames;
    }

    /// <summary>Reads exactly dst.Length/2 frames if available, otherwise as many as available. Returns frames read.</summary>
    public int Read(Span<float> dst)
    {
        int frames = dst.Length / 2;
        long r = _readFrames;
        long w = Volatile.Read(ref _writeFrames);
        int avail = (int)(w - r);
        if (frames > avail) frames = avail;
        if (frames <= 0) return 0;

        int start = (int)(r % _capacityFrames);
        int first = Math.Min(frames, _capacityFrames - start);
        _data.AsSpan(start * 2, first * 2).CopyTo(dst);
        if (first < frames)
            _data.AsSpan(0, (frames - first) * 2).CopyTo(dst.Slice(first * 2));

        Volatile.Write(ref _readFrames, r + frames);
        return frames;
    }

    /// <summary>Consumer-side: discard frames.</summary>
    public int Skip(int frames)
    {
        long r = _readFrames;
        long w = Volatile.Read(ref _writeFrames);
        int avail = (int)(w - r);
        if (frames > avail) frames = avail;
        if (frames <= 0) return 0;
        Volatile.Write(ref _readFrames, r + frames);
        return frames;
    }
}
