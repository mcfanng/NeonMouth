namespace NeonMouth.Audio.Dsp;

/// <summary>RBJ cookbook biquad filter (transposed direct form II).</summary>
public sealed class Biquad
{
    float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    public static Biquad LowPass(int sampleRate, float freq, float q = 0.707f) => new Biquad().SetLowPass(sampleRate, freq, q);
    public static Biquad HighPass(int sampleRate, float freq, float q = 0.707f) => new Biquad().SetHighPass(sampleRate, freq, q);
    public static Biquad BandPass(int sampleRate, float freq, float q) => new Biquad().SetBandPass(sampleRate, freq, q);

    public Biquad SetLowPass(int sampleRate, float freq, float q)
    {
        var (w, alpha) = Params(sampleRate, freq, q);
        float cos = MathF.Cos(w);
        return Set((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public Biquad SetHighPass(int sampleRate, float freq, float q)
    {
        var (w, alpha) = Params(sampleRate, freq, q);
        float cos = MathF.Cos(w);
        return Set((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public Biquad SetBandPass(int sampleRate, float freq, float q)
    {
        var (w, alpha) = Params(sampleRate, freq, q);
        float cos = MathF.Cos(w);
        return Set(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    static (float w, float alpha) Params(int sampleRate, float freq, float q)
    {
        freq = Math.Clamp(freq, 10f, sampleRate * 0.45f);
        float w = 2 * MathF.PI * freq / sampleRate;
        return (w, MathF.Sin(w) / (2 * q));
    }

    Biquad Set(float b0, float b1, float b2, float a0, float a1, float a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        return this;
    }

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset() => _z1 = _z2 = 0;
}
