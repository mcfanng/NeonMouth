namespace NeonMouth.Audio.Dsp;

/// <summary>
/// Real-time feedback (Larsen) detection and ducking (§4.1). Every hop it takes a windowed FFT of
/// the signal and looks for one narrow peak towering over the rest of the spectrum at the same
/// frequency for several consecutive windows, with no harmonics (voice and singing are harmonic
/// series; feedback is a lone sine). On detection <see cref="DuckGain"/> drops quickly to a floor (never to
/// silence) and recovers gradually once the howl has been gone for a while.
/// Allocation-free after construction; call <see cref="Process"/> on the audio thread.
/// </summary>
public sealed class HowlingDetector
{
    const int FftSize = 1024;
    const int Hop = FftSize / 2;
    const float PeakToAverageDb = 24f;     // howl peak vs mean of the other bins
    const float HarmonicRejectDb = 25f;    // a harmonic within this of the peak means voice, not howl
    const float MinPeakDbfs = -45f;        // ignore quiet tones (hum, noise floor)
    const int FramesToTrigger = 8;         // ~85 ms at 48 kHz
    const float DuckStepDb = -8f;          // each fresh detection while ducked goes further down
    const float DuckFloorDb = -24f;
    const float HoldSeconds = 1.5f;
    const float RecoverDbPerSecond = 6f;

    readonly int _sampleRate;
    readonly float[] _ring = new float[FftSize];
    readonly float[] _re = new float[FftSize];
    readonly float[] _im = new float[FftSize];
    readonly float[] _window = new float[FftSize];
    readonly int[] _bitReverse = new int[FftSize];
    readonly float[] _cos = new float[FftSize / 2];
    readonly float[] _sin = new float[FftSize / 2];
    readonly int _maxBin;
    readonly float _windowPowerGain;

    int _writePos;
    int _sinceLastAnalysis;
    int _lastPeakBin = -10;
    int _consecutive;
    float _duckDb;           // current target attenuation, ≤ 0
    float _currentGain = 1f; // smoothed linear gain actually applied
    int _holdSamples;

    public HowlingDetector(int sampleRate)
    {
        _sampleRate = sampleRate;
        int bits = (int)Math.Log2(FftSize);
        float windowSum = 0f;
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FftSize - 1));
            windowSum += _window[i];
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _bitReverse[i] = r;
        }
        for (int i = 0; i < FftSize / 2; i++)
        {
            _cos[i] = MathF.Cos(-2f * MathF.PI * i / FftSize);
            _sin[i] = MathF.Sin(-2f * MathF.PI * i / FftSize);
        }
        // A full-scale sine's bin magnitude is amplitude * windowSum / 2.
        _windowPowerGain = windowSum * windowSum / 4f;
        // Feedback lives roughly in 100 Hz..10 kHz; skip DC/rumble and the top end.
        _maxBin = Math.Min(FftSize / 2 - 2, (int)(10_000f * FftSize / sampleRate));
    }

    public bool Enabled { get; set; } = true;

    /// <summary>True while output is reduced because of feedback (for the protection indicator).</summary>
    public bool IsDucking => _duckDb < -0.5f || _currentGain < 0.94f;

    /// <summary>Frequency of the last detected howl in Hz, or 0.</summary>
    public float LastHowlHz { get; private set; }

    /// <summary>Linear gain to apply to the output this block.</summary>
    public float DuckGain => _currentGain;

    /// <summary>Analyse a block and advance the duck envelope. Does not modify the block.</summary>
    public void Process(ReadOnlySpan<float> block)
    {
        if (!Enabled)
        {
            _duckDb = 0f;
            _currentGain = 1f;
            return;
        }

        for (int i = 0; i < block.Length; i++)
        {
            _ring[_writePos] = block[i];
            _writePos = (_writePos + 1) & (FftSize - 1);
            if (++_sinceLastAnalysis >= Hop)
            {
                _sinceLastAnalysis = 0;
                Analyse();
            }
        }

        // Envelope: hold after the last detection, then recover slowly. Attack is fast but ramped
        // over the block so there's no click.
        if (_holdSamples > 0) _holdSamples -= block.Length;
        else if (_duckDb < 0f) _duckDb = MathF.Min(0f, _duckDb + RecoverDbPerSecond * block.Length / _sampleRate);

        float target = OutputLimiter.DbToLinear(_duckDb);
        float coeff = target < _currentGain ? 0.5f : 1f; // fast down, recovery already slow in dB
        _currentGain += (target - _currentGain) * coeff;
    }

    public void Reset()
    {
        Array.Clear(_ring);
        _writePos = 0;
        _sinceLastAnalysis = 0;
        _consecutive = 0;
        _lastPeakBin = -10;
        _duckDb = 0f;
        _currentGain = 1f;
        _holdSamples = 0;
        LastHowlHz = 0f;
    }

    void Analyse()
    {
        for (int i = 0; i < FftSize; i++)
        {
            int src = (_writePos + i) & (FftSize - 1);
            int dst = _bitReverse[i];
            _re[dst] = _ring[src] * _window[i];
            _im[dst] = 0f;
        }
        Fft();

        int minBin = Math.Max(2, (int)(100f * FftSize / _sampleRate));
        int peakBin = minBin;
        float peak = 0f, total = 0f;
        for (int k = minBin; k <= _maxBin; k++)
        {
            float p = _re[k] * _re[k] + _im[k] * _im[k];
            _re[k] = p; // reuse as power spectrum
            total += p;
            if (p > peak) { peak = p; peakBin = k; }
        }

        // A windowed sine leaks into its neighbours, so count ±2 bins as the peak.
        float peakBand = 0f;
        for (int k = peakBin - 2; k <= peakBin + 2; k++)
            if (k >= minBin && k <= _maxBin) peakBand += _re[k];
        int others = _maxBin - minBin + 1 - 5;
        float mean = (total - peakBand) / others;

        float peakDbfs = 10f * MathF.Log10(peakBand / _windowPowerGain + 1e-12f);
        float ratioDb = 10f * MathF.Log10(peakBand / (mean + 1e-20f));
        bool tonal = peakDbfs > MinPeakDbfs && ratioDb > PeakToAverageDb && !HasHarmonics(peakBin, peakBand, minBin);

        if (tonal && Math.Abs(peakBin - _lastPeakBin) <= 1) _consecutive++;
        else _consecutive = tonal ? 1 : 0;
        _lastPeakBin = tonal ? peakBin : -10;

        if (_consecutive >= FramesToTrigger)
        {
            // Further detections while still howling duck deeper, once per trigger period.
            _consecutive = 0;
            _duckDb = MathF.Max(DuckFloorDb, MathF.Min(_duckDb, 0f) + DuckStepDb);
            _holdSamples = (int)(HoldSeconds * _sampleRate);
            LastHowlHz = peakBin * (float)_sampleRate / FftSize;
        }
    }

    /// <summary>
    /// Voiced sound (speech, singing) is a harmonic series; feedback is a lone sine. The peak may be
    /// the fundamental or a formant-boosted harmonic, so look above (2×, 3×) and below (½, ⅓) it.
    /// </summary>
    bool HasHarmonics(int peakBin, float peakBand, int minBin)
    {
        float threshold = peakBand * OutputLimiter.DbToLinear(-HarmonicRejectDb) * OutputLimiter.DbToLinear(-HarmonicRejectDb);
        ReadOnlySpan<float> ratios = [2f, 3f, 0.5f, 1f / 3f, 1.5f];
        foreach (float r in ratios)
        {
            int center = (int)MathF.Round(peakBin * r);
            float band = 0f;
            for (int k = center - 2; k <= center + 2; k++)
                if (k >= minBin && k <= _maxBin) band += _re[k];
            if (band > threshold && Math.Abs(center - peakBin) > 4) return true;
        }
        return false;
    }

    void Fft()
    {
        for (int size = 2; size <= FftSize; size <<= 1)
        {
            int half = size >> 1;
            int step = FftSize / size;
            for (int start = 0; start < FftSize; start += size)
            {
                for (int j = 0; j < half; j++)
                {
                    float wr = _cos[j * step], wi = _sin[j * step];
                    int a = start + j, b = a + half;
                    float tr = _re[b] * wr - _im[b] * wi;
                    float ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti;
                    _re[a] += tr; _im[a] += ti;
                }
            }
        }
    }
}
