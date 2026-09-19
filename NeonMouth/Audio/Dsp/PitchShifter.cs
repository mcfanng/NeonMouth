namespace NeonMouth.Audio.Dsp;

/// <summary>
/// Low-latency time-domain pitch shifter: two read taps sweep through a short delay line
/// with complementary sin² crossfades. Allocation-free after construction.
/// Added latency is roughly half the window length.
/// </summary>
public sealed class PitchShifter
{
    readonly float[] _buffer;
    readonly int _window;
    int _writePos;
    double _phase;

    public float Ratio { get; set; } = 1f;

    public PitchShifter(int sampleRate, float windowMs = 40f)
    {
        _window = Math.Max(64, (int)(sampleRate * windowMs / 1000f));
        _buffer = new float[_window * 2 + 4];
    }

    public float Process(float input)
    {
        int len = _buffer.Length;
        _buffer[_writePos] = input;

        _phase += (1.0 - Ratio) / _window;
        if (_phase >= 1.0) _phase -= 1.0;
        else if (_phase < 0.0) _phase += 1.0;

        double phase2 = _phase + 0.5;
        if (phase2 >= 1.0) phase2 -= 1.0;

        float a = Tap(_phase * _window, len);
        float b = Tap(phase2 * _window, len);

        float ga = MathF.Sin(MathF.PI * (float)_phase);
        ga *= ga;
        float output = a * ga + b * (1f - ga);

        if (++_writePos >= len) _writePos = 0;
        return output;
    }

    float Tap(double delay, int len)
    {
        double readPos = _writePos - delay - 1;
        while (readPos < 0) readPos += len;
        int i0 = (int)readPos;
        int i1 = i0 + 1 >= len ? 0 : i0 + 1;
        float frac = (float)(readPos - i0);
        return _buffer[i0] + (_buffer[i1] - _buffer[i0]) * frac;
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        _writePos = 0;
        _phase = 0;
    }
}
