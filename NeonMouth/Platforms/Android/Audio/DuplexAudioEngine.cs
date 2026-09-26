using System.Runtime.Versioning;
using NeonMouth.Audio.Dsp;
using static NeonMouth.Platforms.Android.Audio.AAudioNative;

namespace NeonMouth.Platforms.Android.Audio;

/// <summary>
/// Full-duplex AAudio engine. A dedicated audio thread reads available input non-blockingly, runs the
/// <see cref="VoiceProcessor"/>, and does a blocking write of one burst to the output, which paces
/// the loop. The loop must stay allocation-free.
/// </summary>
/// <remarks>
/// AAudio data/error callbacks are deliberately not used. AAudio calls them through a CFI-checked
/// indirect call, and the runtime-generated entry point of an [UnmanagedCallersOnly] method can fail
/// that check, which kills the process with SIGTRAP in <c>__cfi_check_fail</c> (seen on a Pixel 7a,
/// Android 17). With blocking read/write no native code ever calls into managed code.
/// </remarks>
[SupportedOSPlatform("android31.0")]
sealed unsafe class DuplexAudioEngine : IDisposable
{
    const int DrainBursts = 10;
    /// <summary>Upper limit for output buffer growth (about 16 ms at a 96-frame burst).</summary>
    const int MaxBufferBursts = 8;
    const long WriteTimeoutNanos = 100_000_000;

    nint _input;
    nint _output;
    Thread? _thread;
    float[] _inBuffer = [];
    float[] _outBuffer = [];
    int _outputChannels;
    volatile bool _muted;
    volatile bool _stopRequested;

    public VoiceProcessor? Processor { get; private set; }
    public int SampleRate { get; private set; }
    public int FramesPerBurst { get; private set; }
    public bool IsRunning => _output != 0;

    /// <summary>Raised on the audio thread when a stream reports an error (e.g. device disconnected).</summary>
    public event Action<int>? StreamError;

    /// <param name="inputPreset">Bluetooth mics use voice recognition, which gets the low-latency path.</param>
    public void Start(int inputDeviceId, int outputDeviceId, int inputPreset = InputPresetVoicePerformance)
    {
        if (IsRunning) throw new InvalidOperationException("Engine already running.");
        try
        {
            _output = OpenOutput(outputDeviceId);
            SampleRate = AAudioStream_getSampleRate(_output);
            _outputChannels = AAudioStream_getChannelCount(_output);
            FramesPerBurst = AAudioStream_getFramesPerBurst(_output);
            AAudioStream_setBufferSizeInFrames(_output, FramesPerBurst * 2);

            // Input opened at the output's rate so no resampling happens on our side.
            _input = OpenInput(inputDeviceId, SampleRate, inputPreset);

            _inBuffer = new float[FramesPerBurst];
            _outBuffer = new float[FramesPerBurst * _outputChannels];
            Processor = new VoiceProcessor(SampleRate);
            _muted = false;
            _stopRequested = false;

            LogStream("input", _input, inputDeviceId);
            LogStream("output", _output, outputDeviceId);

            Check(AAudioStream_requestStart(_input), "start input");
            Check(AAudioStream_requestStart(_output), "start output");

            _thread = new Thread(AudioLoop) { Name = "NeonMouthAudio", IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Silences output immediately; safe to call from any thread.</summary>
    public void Mute() => _muted = true;

    public void Stop()
    {
        _muted = true;
        _stopRequested = true;
        // The loop wakes at least every WriteTimeoutNanos, so the join is bounded. Streams must not be
        // closed while the audio thread may still be inside a read or write.
        if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join();
        _thread = null;
        CloseStream(ref _output);
        CloseStream(ref _input);
    }

    /// <summary>Device IDs AAudio actually opened (may differ from the requested ones).</summary>
    public (int Input, int Output) ActualDeviceIds =>
        (_input == 0 ? 0 : AAudioStream_getDeviceId(_input), _output == 0 ? 0 : AAudioStream_getDeviceId(_output));

    public void Dispose() => Stop();

    static nint OpenOutput(int deviceId)
    {
        Check(AAudio_createStreamBuilder(out var b), "create builder");
        try
        {
            AAudioStreamBuilder_setDirection(b, DirectionOutput);
            AAudioStreamBuilder_setDeviceId(b, deviceId);
            AAudioStreamBuilder_setFormat(b, FormatPcmFloat);
            AAudioStreamBuilder_setSharingMode(b, SharingModeExclusive);
            AAudioStreamBuilder_setPerformanceMode(b, PerformanceModeLowLatency);
            AAudioStreamBuilder_setUsage(b, UsageMedia);
            AAudioStreamBuilder_setContentType(b, ContentTypeSpeech);
            Check(AAudioStreamBuilder_openStream(b, out var stream), "open output");
            return stream;
        }
        finally { AAudioStreamBuilder_delete(b); }
    }

    static nint OpenInput(int deviceId, int sampleRate, int inputPreset)
    {
        Check(AAudio_createStreamBuilder(out var b), "create builder");
        try
        {
            AAudioStreamBuilder_setDirection(b, DirectionInput);
            AAudioStreamBuilder_setDeviceId(b, deviceId);
            AAudioStreamBuilder_setFormat(b, FormatPcmFloat);
            AAudioStreamBuilder_setChannelCount(b, 1);
            AAudioStreamBuilder_setSampleRate(b, sampleRate);
            AAudioStreamBuilder_setSharingMode(b, SharingModeExclusive);
            AAudioStreamBuilder_setPerformanceMode(b, PerformanceModeLowLatency);
            AAudioStreamBuilder_setInputPreset(b, inputPreset);
            Check(AAudioStreamBuilder_openStream(b, out var stream), "open input");
            return stream;
        }
        finally { AAudioStreamBuilder_delete(b); }
    }

    static void CloseStream(ref nint stream)
    {
        if (stream == 0) return;
        AAudioStream_requestStop(stream);
        AAudioStream_close(stream);
        stream = 0;
    }

    static void LogStream(string what, nint stream, int requestedDeviceId) =>
        global::Android.Util.Log.Info("NeonMouth",
            $"AAudio {what}: device={AAudioStream_getDeviceId(stream)} (requested {requestedDeviceId}) " +
            $"rate={AAudioStream_getSampleRate(stream)} ch={AAudioStream_getChannelCount(stream)} " +
            $"sharing={(AAudioStream_getSharingMode(stream) == SharingModeExclusive ? "exclusive" : "shared")} " +
            $"perf={AAudioStream_getPerformanceMode(stream)} burst={AAudioStream_getFramesPerBurst(stream)}");

    static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"AAudio {what} failed: {ResultText(result)}");
    }

    void AudioLoop()
    {
        try { global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentAudio); }
        catch (Exception) { /* Best effort; Thread.Priority already raised it. */ }

        int error = RunLoop();
        if (error < 0 && !_stopRequested)
        {
            _muted = true;
            // Streams must not be closed from this thread; the listener hops threads.
            StreamError?.Invoke(error);
        }
    }

    /// <summary>Runs until Stop or a stream error; returns the AAudio error, or 0 when stopped.</summary>
    int RunLoop()
    {
        nint input = _input, output = _output;
        int frames = FramesPerBurst;
        int channels = _outputChannels;
        var processor = Processor!;
        // Diagnostics, logged about once a second: raw mic peak and how much input actually arrived.
        int statsBursts = Math.Max(1, SampleRate / frames);
        float rawPeak = 0f;
        long framesRead = 0;
        // Output buffer tuning: at most one step per ~100 ms, so a single glitch grows it by one burst.
        int maxBuffer = Math.Min(AAudioStream_getBufferCapacityInFrames(output), frames * MaxBufferBursts);
        int tuneCooldown = Math.Max(1, SampleRate / 10 / frames);
        int nextTuneBurst = DrainBursts;
        int xruns = 0;
        // Input queue cap: the smallest queue seen over a second is input that never drains.
        long minQueue = long.MaxValue;

        fixed (float* inBuf = _inBuffer)
        fixed (float* outBuf = _outBuffer)
        {
            var block = new Span<float>(inBuf, frames);
            var outSpan = new Span<float>(outBuf, frames * channels);

            for (int burst = 0; !_stopRequested; burst++)
            {
                int read;
                if (burst < DrainBursts)
                {
                    // Drain stale input at startup so latency doesn't accumulate in the input buffer.
                    while ((read = AAudioStream_read(input, inBuf, frames, 0)) > 0) { }
                    if (read < 0) return read;
                    outSpan.Clear();
                }
                else
                {
                    // Late input is padded with silence and the late frames then queue up for good, so
                    // each hiccup would add delay (seen with Bluetooth mics). Drop that backlog to one burst.
                    if (burst % statsBursts == 0 && minQueue != long.MaxValue)
                    {
                        if (minQueue - frames >= frames) TrimInput(input, inBuf, minQueue - frames, frames);
                        minQueue = long.MaxValue;
                    }

                    read = AAudioStream_read(input, inBuf, frames, 0);
                    if (read < 0) return read;
                    minQueue = Math.Min(minQueue, AAudioStream_getFramesWritten(input) - AAudioStream_getFramesRead(input));
                    if (read < frames) block[read..].Clear();
                    framesRead += read;
                    for (int f = 0; f < read; f++) rawPeak = Math.Max(rawPeak, Math.Abs(block[f]));
                    if (burst % statsBursts == 0)
                    {
                        global::Android.Util.Log.Info("NeonMouth",
                            $"Input: peak={rawPeak:0.00000} frames={framesRead}/{statsBursts * frames}");
                        rawPeak = 0f;
                        framesRead = 0;
                    }

                    if (_muted)
                    {
                        outSpan.Clear();
                    }
                    else
                    {
                        processor.Process(block);
                        if (channels == 1)
                        {
                            block.CopyTo(outSpan);
                        }
                        else
                        {
                            for (int f = 0, o = 0; f < frames; f++)
                            {
                                float s = block[f];
                                for (int c = 0; c < channels; c++) outSpan[o++] = s;
                            }
                        }
                    }
                }

                // Blocks until there is room for a burst, which paces the loop to the output clock.
                int written = AAudioStream_write(output, outBuf, frames, WriteTimeoutNanos);
                if (written < 0 && written != AAUDIO_ERROR_TIMEOUT) return written;

                // Grow the output buffer one burst at a time while underruns continue (Android's
                // recommended tuning); each step adds one burst of latency. Startup underruns are ignored.
                if (burst == DrainBursts)
                {
                    xruns = AAudioStream_getXRunCount(output);
                }
                else if (burst >= nextTuneBurst)
                {
                    int now = AAudioStream_getXRunCount(output);
                    if (now > xruns)
                    {
                        int size = AAudioStream_getBufferSizeInFrames(output);
                        if (size + frames <= maxBuffer)
                        {
                            AAudioStream_setBufferSizeInFrames(output, size + frames);
                            global::Android.Util.Log.Info("NeonMouth", $"Output underrun: buffer {size} -> {size + frames} frames");
                        }
                        nextTuneBurst = burst + tuneCooldown;
                    }
                    xruns = now;
                }
            }
        }
        return 0;
    }

    static void TrimInput(nint input, float* scratch, long excess, int frames)
    {
        long dropped = 0;
        while (dropped < excess)
        {
            int n = AAudioStream_read(input, scratch, (int)Math.Min(frames, excess - dropped), 0);
            if (n <= 0) break;
            dropped += n;
        }
        global::Android.Util.Log.Info("NeonMouth", $"Input queue trimmed: dropped {dropped} frames");
    }
}
