namespace WinMusic.Core.Dsp;

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
