namespace NeonMouth.Audio.Dsp;

/// <summary>
/// Real-time feedback (Larsen) detection and suppression (§4.1). Every hop it takes a windowed FFT
/// of the signal and looks for one narrow peak towering over the rest of the spectrum at the same
/// frequency for several consecutive windows, with no harmonics (voice and singing are harmonic
/// series; feedback is a lone sine).
/// <para>
/// A detection places a narrow notch at the howling frequency, deepening it on repeats, so only
/// that pitch is cut and the rest of the sound keeps its level. Notches hold for a while (otherwise
/// the howl rebuilds within seconds) and then fade out. There is deliberately no whole-output duck:
/// device tests showed a full-depth notch always breaks a real feedback loop, so a tone that
/// survives one is the user's own voice (a held sung note), and ducking it only frustrates.
/// </para>
/// Allocation-free after construction; call <see cref="Process"/> on the audio thread.
/// </summary>
public sealed class HowlingDetector
{
    const int FftSize = 1024;
    const int Hop = FftSize / 2;
    const float PeakToAverageDb = 24f;     // howl peak vs mean of the other bins
    const float HighPeakToAverageDb = 30f; // stricter above HighFrequencyHz, where sibilants live
    const float HighFrequencyHz = 4000f;
    const float HarmonicRejectDb = 25f;    // a harmonic within this of the peak means voice, not howl
    const float MinPeakDbfs = -45f;        // ignore quiet tones (hum, noise floor)
    const int FramesToTrigger = 8;         // ~85 ms at 48 kHz

    const int MaxNotches = 4;
    const float NotchStepDb = -10f;        // each detection at a notched frequency goes deeper
    const float NotchFloorDb = -30f;
    const float NotchStepSeconds = 0.4f;   // let a notch act (through Bluetooth delay) before deepening
    const float NotchHoldSeconds = 20f;
    const float NotchReleaseDbPerSecond = 0.5f;
    const float NotchBandwidthHz = 40f;    // ~1 FFT bin; the frequency estimate is good to a few Hz

    const float IndicatorSeconds = 3f;     // how long the UI shows protection after a detection

    readonly int _sampleRate;
    readonly float[] _ring = new float[FftSize];
    readonly float[] _re = new float[FftSize];
    readonly float[] _im = new float[FftSize];
    readonly float[] _window = new float[FftSize];
    readonly int[] _bitReverse = new int[FftSize];
    readonly float[] _cos = new float[FftSize / 2];
    readonly float[] _sin = new float[FftSize / 2];
    readonly int _maxBin;
    readonly int _highBin;
    readonly float _windowPowerGain;

    readonly Biquad[] _notch = new Biquad[MaxNotches];
    readonly float[] _notchHz = new float[MaxNotches];
    readonly float[] _notchDb = new float[MaxNotches];        // target depth, ≤ 0; 0 = slot free
    readonly float[] _notchAppliedDb = new float[MaxNotches]; // depth the coefficients were set for
    readonly int[] _notchHold = new int[MaxNotches];
    readonly int[] _notchAge = new int[MaxNotches];           // samples since last deepened

    int _writePos;
    int _sinceLastAnalysis;
    int _lastPeakBin = -10;
    int _consecutive;
    int _sinceTrigger = int.MaxValue;

    // --- Temporary Phase 3 tuning diagnostics. Remove once thresholds are settled. ---
    /// <summary>Set from the platform layer to log detector decisions; null disables all logging.</summary>
    public static Action<string>? Diagnostics;
    int _hopsSinceLog;
    float _worstRatioDb = float.NegativeInfinity;
    float _worstPeakDbfs;
    int _worstBin;
    bool _worstLoud, _worstNarrow, _worstHarmonic;
    int _tonalHops;

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
        for (int i = 0; i < MaxNotches; i++) _notch[i] = new Biquad();
        // A full-scale sine's bin magnitude is amplitude * windowSum / 2.
        _windowPowerGain = windowSum * windowSum / 4f;
        // Feedback lives roughly in 100 Hz..10 kHz; skip DC/rumble and the top end.
        _maxBin = Math.Min(FftSize / 2 - 2, (int)(10_000f * FftSize / sampleRate));
        _highBin = (int)(HighFrequencyHz * FftSize / sampleRate);
    }

    /// <summary>Feedback protection on/off (user setting). Turning it off clears the notches.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True for a few seconds after a detection (for the protection indicator).</summary>
    public bool IsSuppressing => _sinceTrigger < IndicatorSeconds * _sampleRate;

    /// <summary>Frequency of the last detected howl in Hz, or 0.</summary>
    public float LastHowlHz { get; private set; }

    /// <summary>Analyse a block, advance the notch envelopes, then notch the block in place.</summary>
    public void Process(Span<float> block)
    {
        if (!Enabled)
        {
            if (_sinceTrigger != int.MaxValue || _notchDb.AsSpan().IndexOfAnyExcept(0f) >= 0)
                Reset();
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
        if (_sinceTrigger < int.MaxValue - block.Length) _sinceTrigger += block.Length;
        else _sinceTrigger = int.MaxValue;

        for (int n = 0; n < MaxNotches; n++)
        {
            if (_notchDb[n] == 0f) continue;
            if (_notchAge[n] < int.MaxValue - block.Length) _notchAge[n] += block.Length;
            if (_notchHold[n] > 0) _notchHold[n] -= block.Length;
            else _notchDb[n] = MathF.Min(0f, _notchDb[n] + NotchReleaseDbPerSecond * block.Length / _sampleRate);
            if (_notchDb[n] == 0f) continue; // released; the slot is free again

            // Recomputing coefficients costs a few transcendentals; only do it in ~0.1 dB steps.
            if (MathF.Abs(_notchDb[n] - _notchAppliedDb[n]) > 0.1f)
            {
                _notchAppliedDb[n] = _notchDb[n];
                float q = Math.Clamp(_notchHz[n] / NotchBandwidthHz, 4f, 20f);
                _notch[n].SetPeaking(_sampleRate, _notchHz[n], q, _notchDb[n]);
            }

            var filter = _notch[n];
            for (int i = 0; i < block.Length; i++) block[i] = filter.Process(block[i]);
        }
    }

    public void Reset()
    {
        Array.Clear(_ring);
        _writePos = 0;
        _sinceLastAnalysis = 0;
        _consecutive = 0;
        _lastPeakBin = -10;
        _sinceTrigger = int.MaxValue;
        Array.Clear(_notchDb);
        Array.Clear(_notchAppliedDb);
        foreach (var f in _notch) f.Reset();
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
        // Split out so the diagnostics can say which clause rejected a candidate; short-circuit
        // order is unchanged.
        bool loud = peakDbfs > MinPeakDbfs;
        bool narrow = ratioDb > (peakBin >= _highBin ? HighPeakToAverageDb : PeakToAverageDb);
        bool harmonic = loud && narrow && HasHarmonics(peakBin, peakBand, minBin);
        bool tonal = loud && narrow && !harmonic;

        if (Diagnostics is not null)
            TrackDiagnostics(peakBin, peakDbfs, ratioDb, loud, narrow, harmonic, tonal);

        if (tonal && Math.Abs(peakBin - _lastPeakBin) <= 1) _consecutive++;
        else _consecutive = tonal ? 1 : 0;
        _lastPeakBin = tonal ? peakBin : -10;

        if (_consecutive >= FramesToTrigger)
        {
            _consecutive = 0;
            float hz = InterpolatePeakHz(peakBin, minBin);
            string action = Suppress(hz);
            Diagnostics?.Invoke($"HOWL TRIGGER {hz:0} Hz (bin {peakBin}) " +
                $"peak={peakDbfs:0.0} dBFS ratio={ratioDb:0.0} dB -> {action}");
        }
    }

    /// <summary>Place or deepen a notch at <paramref name="hz"/>. Returns a log description.</summary>
    string Suppress(float hz)
    {
        int slot = -1;
        for (int n = 0; n < MaxNotches; n++)
            if (_notchDb[n] < 0f && MathF.Abs(_notchHz[n] - hz) < MathF.Max(NotchBandwidthHz, 0.05f * hz))
                slot = n;

        if (slot >= 0 && (_notchAge[slot] < NotchStepSeconds * _sampleRate || _notchDb[slot] <= NotchFloorDb + 0.5f))
        {
            // Still taking effect, or already at full depth (then it's a held note, not feedback).
            _notchHold[slot] = (int)(NotchHoldSeconds * _sampleRate);
            return Diagnostics is null ? "" : $"notch {_notchHz[slot]:0} Hz {_notchDb[slot]:0} dB (kept)";
        }

        _sinceTrigger = 0;
        LastHowlHz = hz;
        if (slot < 0)
        {
            // A free slot, or else the shallowest notch (the one least needed).
            slot = 0;
            for (int n = 1; n < MaxNotches; n++)
                if (_notchDb[n] > _notchDb[slot]) slot = n;
            if (_notchDb[slot] == 0f) _notch[slot].Reset();
            _notchHz[slot] = hz;
            _notchDb[slot] = 0f;
        }
        else
        {
            _notchHz[slot] = 0.5f * (_notchHz[slot] + hz);
        }
        _notchDb[slot] = MathF.Max(NotchFloorDb, _notchDb[slot] + NotchStepDb);
        _notchHold[slot] = (int)(NotchHoldSeconds * _sampleRate);
        _notchAge[slot] = 0;
        _notchAppliedDb[slot] = 1f; // frequency may have moved: force new coefficients
        return Diagnostics is null ? "" : $"notch {_notchHz[slot]:0} Hz {_notchDb[slot]:0} dB";
    }

    /// <summary>Parabolic interpolation on the log power of the peak and its neighbours.</summary>
    float InterpolatePeakHz(int peakBin, int minBin)
    {
        float offset = 0f;
        if (peakBin > minBin && peakBin < _maxBin)
        {
            float a = MathF.Log(_re[peakBin - 1] + 1e-20f);
            float b = MathF.Log(_re[peakBin] + 1e-20f);
            float c = MathF.Log(_re[peakBin + 1] + 1e-20f);
            float denom = a - 2f * b + c;
            if (denom < 0f) offset = Math.Clamp(0.5f * (a - c) / denom, -0.5f, 0.5f);
        }
        return (peakBin + offset) * _sampleRate / FftSize;
    }

    /// <summary>
    /// Rate-limited tuning output: the strongest candidate seen each second, and how the test judged
    /// it. Allocates only on the ~1 Hz log line, matching the existing per-second input logging.
    /// </summary>
    void TrackDiagnostics(int peakBin, float peakDbfs, float ratioDb, bool loud, bool narrow, bool harmonic, bool tonal)
    {
        if (tonal) _tonalHops++;
        if (ratioDb > _worstRatioDb)
        {
            _worstRatioDb = ratioDb;
            _worstPeakDbfs = peakDbfs;
            _worstBin = peakBin;
            _worstLoud = loud;
            _worstNarrow = narrow;
            _worstHarmonic = harmonic;
        }

        int hopsPerSecond = Math.Max(1, _sampleRate / Hop);
        if (++_hopsSinceLog < hopsPerSecond) return;

        string verdict = !_worstLoud ? $"quiet (<{MinPeakDbfs:0} dBFS)"
            : !_worstNarrow ? "broadband"
            : _worstHarmonic ? "harmonic -> voice"
            : "TONAL";
        string notches = "";
        for (int n = 0; n < MaxNotches; n++)
            if (_notchDb[n] < 0f) notches += $" {_notchHz[n]:0}Hz/{_notchDb[n]:0.0}";
        Diagnostics?.Invoke(
            $"Howl: top {_worstBin * (float)_sampleRate / FftSize:0} Hz " +
            $"peak={_worstPeakDbfs:0.0} dBFS ratio={_worstRatioDb:0.0} dB {verdict}; " +
            $"tonal {_tonalHops}/{hopsPerSecond} hops, run={_consecutive}/{FramesToTrigger}, " +
            $"notches:{(notches.Length == 0 ? " none" : notches)}");

        _hopsSinceLog = 0;
        _tonalHops = 0;
        _worstRatioDb = float.NegativeInfinity;
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
