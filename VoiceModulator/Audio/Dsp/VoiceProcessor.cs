namespace VoiceModulator.Audio.Dsp;

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
        for (int i = 0; i < block.Length; i++) block[i] *= gain;

        _effects[_currentIndex].Process(block, Math.Clamp(Intensity, 0f, 1f));
        Limiter.Process(block);
    }

    public void Reset()
    {
        foreach (var e in _effects) e.Reset();
        Limiter.Reset();
    }
}
