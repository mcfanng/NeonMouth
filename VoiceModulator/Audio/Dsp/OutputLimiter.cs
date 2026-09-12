namespace VoiceModulator.Audio.Dsp;

/// <summary>
/// Final safety stage (§4.1): applies the user gain ceiling, then a peak limiter with instant
/// attack and smooth release, then a hard clip so output can never exceed <see cref="HardCeiling"/>.
/// </summary>
public sealed class OutputLimiter
{
    public const float HardCeiling = 0.95f;
    const float Threshold = 0.8f;

    readonly float _releaseCoeff;
    float _envelopeGain = 1f;

    /// <summary>Linear gain derived from the user's ceiling setting (0..1).</summary>
    public float UserGain { get; set; } = 0.5f;

    /// <summary>Additional ducking gain (used by howling protection in a later phase).</summary>
    public float DuckGain { get; set; } = 1f;

    public OutputLimiter(int sampleRate, float releaseMs = 150f)
    {
        _releaseCoeff = MathF.Exp(-1f / (sampleRate * releaseMs / 1000f));
    }

    public static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

    public void Process(Span<float> block)
    {
        float pre = Math.Clamp(UserGain, 0f, 1f) * Math.Clamp(DuckGain, 0f, 1f);
        for (int i = 0; i < block.Length; i++)
        {
            float s = block[i] * pre;
            float peak = MathF.Abs(s);
            float target = peak > Threshold ? Threshold / peak : 1f;

            if (target < _envelopeGain) _envelopeGain = target;
            else _envelopeGain = target + (_envelopeGain - target) * _releaseCoeff;

            if (!float.IsFinite(_envelopeGain)) _envelopeGain = 1f;
            s *= _envelopeGain;
            block[i] = Math.Clamp(s, -HardCeiling, HardCeiling);
        }
    }

    public void Reset() => _envelopeGain = 1f;
}
