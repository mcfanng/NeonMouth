namespace NeonMouth.Audio.Dsp;

public enum VoiceEffectKind
{
    Natural,
    Witch,
    Cartoon,
    Monster,
    Robot,
}

/// <summary>
/// A voice effect processes a mono block in place. Implementations must not allocate in
/// <see cref="Process"/> since it runs on the real-time audio thread.
/// </summary>
public interface IVoiceEffect
{
    VoiceEffectKind Kind { get; }

    /// <summary>0..1 blend of effect strength.</summary>
    void Process(Span<float> block, float intensity);

    void Reset();
}

public static class VoiceEffectFactory
{
    public static IVoiceEffect Create(VoiceEffectKind kind, int sampleRate) => kind switch
    {
        VoiceEffectKind.Natural => new NaturalEffect(),
        VoiceEffectKind.Witch => new WitchEffect(sampleRate),
        VoiceEffectKind.Cartoon => new CartoonEffect(sampleRate),
        VoiceEffectKind.Monster => new MonsterEffect(sampleRate),
        VoiceEffectKind.Robot => new RobotEffect(sampleRate),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Average delay an effect adds: half its pitch-shifter window (§4.2 estimate).</summary>
    public static double AddedLatencyMs(VoiceEffectKind kind) => kind switch
    {
        VoiceEffectKind.Witch => WitchEffect.PitchWindowMs / 2,
        VoiceEffectKind.Cartoon => CartoonEffect.PitchWindowMs / 2,
        VoiceEffectKind.Monster => MonsterEffect.PitchWindowMs / 2,
        _ => 0,
    };
}

sealed class NaturalEffect : IVoiceEffect
{
    public VoiceEffectKind Kind => VoiceEffectKind.Natural;
    public void Process(Span<float> block, float intensity) { }
    public void Reset() { }
}

/// <summary>High pitch with raspy distortion and a nasal band emphasis.</summary>
sealed class WitchEffect(int sampleRate) : IVoiceEffect
{
    public const float PitchWindowMs = 20f;

    readonly PitchShifter _pitch = new(sampleRate, PitchWindowMs);
    readonly Biquad _nasal = Biquad.BandPass(sampleRate, 1800f, 1.2f);
    readonly Biquad _lowCut = Biquad.HighPass(sampleRate, 250f);

    public VoiceEffectKind Kind => VoiceEffectKind.Witch;

    public void Process(Span<float> block, float intensity)
    {
        _pitch.Ratio = 1f + 0.6f * intensity;
        float drive = 1f + 6f * intensity;
        for (int i = 0; i < block.Length; i++)
        {
            float dry = block[i];
            float s = _lowCut.Process(_pitch.Process(dry));
            s = MathF.Tanh(s * drive) / MathF.Tanh(drive);
            s += _nasal.Process(s) * 1.5f * intensity;
            block[i] = s * 0.8f;
        }
    }

    public void Reset() { _pitch.Reset(); _nasal.Reset(); _lowCut.Reset(); }
}

/// <summary>Clean, strongly raised pitch.</summary>
sealed class CartoonEffect(int sampleRate) : IVoiceEffect
{
    public const float PitchWindowMs = 20f;

    readonly PitchShifter _pitch = new(sampleRate, PitchWindowMs);
    readonly Biquad _lowCut = Biquad.HighPass(sampleRate, 180f);

    public VoiceEffectKind Kind => VoiceEffectKind.Cartoon;

    public void Process(Span<float> block, float intensity)
    {
        _pitch.Ratio = 1f + 0.9f * intensity;
        for (int i = 0; i < block.Length; i++)
            block[i] = _lowCut.Process(_pitch.Process(block[i]));
    }

    public void Reset() { _pitch.Reset(); _lowCut.Reset(); }
}

/// <summary>Lowered pitch with a dark, saturated tone.</summary>
sealed class MonsterEffect(int sampleRate) : IVoiceEffect
{
    public const float PitchWindowMs = 30f;

    readonly PitchShifter _pitch = new(sampleRate, PitchWindowMs);
    readonly Biquad _lowPass = Biquad.LowPass(sampleRate, 2200f);
    readonly Biquad _body = Biquad.BandPass(sampleRate, 140f, 0.8f);

    public VoiceEffectKind Kind => VoiceEffectKind.Monster;

    public void Process(Span<float> block, float intensity)
    {
        _pitch.Ratio = 1f - 0.45f * intensity;
        float drive = 1f + 3f * intensity;
        for (int i = 0; i < block.Length; i++)
        {
            float s = _pitch.Process(block[i]);
            s = _lowPass.Process(s);
            s += _body.Process(s) * 2f * intensity;
            block[i] = MathF.Tanh(s * drive) / MathF.Tanh(drive);
        }
    }

    public void Reset() { _pitch.Reset(); _lowPass.Reset(); _body.Reset(); }
}

/// <summary>Ring modulation plus a short metallic comb filter.</summary>
sealed class RobotEffect : IVoiceEffect
{
    readonly int _sampleRate;
    readonly float[] _comb;
    int _combPos;
    double _carrierPhase;

    public RobotEffect(int sampleRate)
    {
        _sampleRate = sampleRate;
        _comb = new float[Math.Max(1, sampleRate * 6 / 1000)];
    }

    public VoiceEffectKind Kind => VoiceEffectKind.Robot;

    public void Process(Span<float> block, float intensity)
    {
        double step = 2 * Math.PI * 70.0 / _sampleRate;
        float feedback = 0.55f * intensity;
        for (int i = 0; i < block.Length; i++)
        {
            float dry = block[i];
            float carrier = (float)Math.Sin(_carrierPhase);
            _carrierPhase += step;
            if (_carrierPhase > 2 * Math.PI) _carrierPhase -= 2 * Math.PI;

            float ring = dry * carrier;
            float s = dry + (ring - dry) * intensity;

            float delayed = _comb[_combPos];
            float y = s + delayed * feedback;
            _comb[_combPos] = y;
            if (++_combPos >= _comb.Length) _combPos = 0;

            block[i] = y * 0.7f;
        }
    }

    public void Reset()
    {
        Array.Clear(_comb);
        _combPos = 0;
        _carrierPhase = 0;
    }
}
