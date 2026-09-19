namespace NeonMouth.Audio.Dsp;

/// <summary>
/// The platform-independent signal chain (§5.3): input gain → effect → output limiter.
/// Howling detection/ducking is inserted in phase 3. Parameters may be changed from any thread;
/// they are read once per block on the audio thread.
/// </summary>
public sealed class VoiceProcessor
{
    readonly IVoiceEffect[] _effects;
    int _currentIndex;
    int _requestedIndex;

    public OutputLimiter Limiter { get; }
    public float InputGain { get; set; } = 1.5f;
    public float Intensity { get; set; } = 0.7f;

    /// <summary>Peak levels of the last block (0..1), for the UI level meters.</summary>
    public float InputPeak { get; private set; }
    public float OutputPeak { get; private set; }

    public VoiceProcessor(int sampleRate)
    {
        _effects = Enum.GetValues<VoiceEffectKind>()
            .Select(k => VoiceEffectFactory.Create(k, sampleRate))
            .ToArray();
        Limiter = new OutputLimiter(sampleRate);
    }

    public VoiceEffectKind Effect
    {
        get => _effects[Volatile.Read(ref _requestedIndex)].Kind;
        set => Volatile.Write(ref _requestedIndex, (int)value);
    }

    public void Process(Span<float> block)
    {
        int requested = Volatile.Read(ref _requestedIndex);
        if (requested != _currentIndex)
        {
            _effects[requested].Reset();
            _currentIndex = requested;
        }

        float gain = InputGain;
        float inPeak = 0f;
        for (int i = 0; i < block.Length; i++)
        {
            block[i] *= gain;
            inPeak = MathF.Max(inPeak, MathF.Abs(block[i]));
        }
        InputPeak = inPeak;

        var effect = _effects[_currentIndex];
        effect.Process(block, Math.Clamp(Intensity, 0f, 1f));

        // A NaN/Inf would permanently poison filter and limiter state (silence until restart),
        // so detect it, drop the block and reset the chain.
        float outPeak = 0f;
        for (int i = 0; i < block.Length; i++)
        {
            float v = block[i];
            if (!float.IsFinite(v))
            {
                block.Clear();
                effect.Reset();
                Limiter.Reset();
                OutputPeak = 0f;
                return;
            }
        }

        Limiter.Process(block);
        for (int i = 0; i < block.Length; i++) outPeak = MathF.Max(outPeak, MathF.Abs(block[i]));
        OutputPeak = outPeak;
    }

    public void Reset()
    {
        foreach (var e in _effects) e.Reset();
        Limiter.Reset();
    }
}
