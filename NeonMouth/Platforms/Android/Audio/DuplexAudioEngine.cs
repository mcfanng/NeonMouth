using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonMouth.Audio.Dsp;
using static NeonMouth.Platforms.Android.Audio.AAudioNative;

namespace NeonMouth.Platforms.Android.Audio;

/// <summary>
/// Full-duplex AAudio engine. The output stream's data callback pulls available input
/// non-blockingly, runs the <see cref="VoiceProcessor"/>, and writes the result. This is the
/// same pattern Oboe's FullDuplexStream uses. The callback path must stay allocation-free.
/// </summary>
[SupportedOSPlatform("android31.0")]
sealed unsafe class DuplexAudioEngine : IDisposable
{
    const int DrainCallbacks = 10;

    nint _input;
    nint _output;
    GCHandle _selfHandle;
    float[] _scratch = [];
    int _outputChannels;
    int _callbackCount;
    volatile bool _muted;

    public VoiceProcessor? Processor { get; private set; }
    public int SampleRate { get; private set; }
    public int FramesPerBurst { get; private set; }
    public bool IsRunning => _output != 0;

    /// <summary>Raised on an AAudio-owned thread when a stream reports an error (e.g. device disconnected).</summary>
    public event Action<int>? StreamError;

    public void Start(int inputDeviceId, int outputDeviceId)
    {
        if (IsRunning) throw new InvalidOperationException("Engine already running.");
        _selfHandle = GCHandle.Alloc(this);
        try
        {
            _output = OpenOutput(outputDeviceId);
            SampleRate = AAudioStream_getSampleRate(_output);
            _outputChannels = AAudioStream_getChannelCount(_output);
            FramesPerBurst = AAudioStream_getFramesPerBurst(_output);
            AAudioStream_setBufferSizeInFrames(_output, FramesPerBurst * 2);

            // Input opened at the output's rate so no resampling happens on our side.
            _input = OpenInput(inputDeviceId, SampleRate);

            // Generous scratch so an oversized callback never forces an allocation.
            _scratch = new float[Math.Max(FramesPerBurst, 256) * 8];
            Processor = new VoiceProcessor(SampleRate);
            _callbackCount = 0;
            _muted = false;

            Check(AAudioStream_requestStart(_input), "start input");
            Check(AAudioStream_requestStart(_output), "start output");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Silences output immediately; safe to call from any thread including callbacks.</summary>
    public void Mute() => _muted = true;

    public void Stop()
    {
        _muted = true;
        CloseStream(ref _output);
        CloseStream(ref _input);
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    public void Dispose() => Stop();

    nint OpenOutput(int deviceId)
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
            AAudioStreamBuilder_setDataCallback(b, &OnData, GCHandle.ToIntPtr(_selfHandle));
            AAudioStreamBuilder_setErrorCallback(b, &OnError, GCHandle.ToIntPtr(_selfHandle));
            Check(AAudioStreamBuilder_openStream(b, out var stream), "open output");
            return stream;
        }
        finally { AAudioStreamBuilder_delete(b); }
    }

    nint OpenInput(int deviceId, int sampleRate)
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
            AAudioStreamBuilder_setInputPreset(b, InputPresetVoicePerformance);
            AAudioStreamBuilder_setErrorCallback(b, &OnError, GCHandle.ToIntPtr(_selfHandle));
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

    static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"AAudio {what} failed: {ResultText(result)}");
    }

    [UnmanagedCallersOnly]
    static int OnData(nint stream, nint userData, nint audioData, int numFrames)
    {
        var engine = (DuplexAudioEngine)GCHandle.FromIntPtr(userData).Target!;
        return engine.Render((float*)audioData, numFrames);
    }

    int Render(float* output, int numFrames)
    {
        int channels = _outputChannels;
        var outSpan = new Span<float>(output, numFrames * channels);
        var input = _input;
        if (input == 0 || numFrames > _scratch.Length)
        {
            outSpan.Clear();
            return CallbackResultContinue;
        }

        fixed (float* scratch = _scratch)
        {
            // Drain stale input at startup so latency doesn't accumulate in the input buffer.
            if (_callbackCount < DrainCallbacks)
            {
                _callbackCount++;
                while (AAudioStream_read(input, scratch, _scratch.Length, 0) > 0) { }
                outSpan.Clear();
                return CallbackResultContinue;
            }

            int read = AAudioStream_read(input, scratch, numFrames, 0);
            if (read < 0) read = 0;
            var block = new Span<float>(scratch, numFrames);
            if (read < numFrames) block[read..].Clear();

            if (_muted)
            {
                outSpan.Clear();
                return CallbackResultContinue;
            }

            Processor!.Process(block);

            if (channels == 1)
            {
                block.CopyTo(outSpan);
            }
            else
            {
                for (int f = 0, o = 0; f < numFrames; f++)
                {
                    float s = block[f];
                    for (int c = 0; c < channels; c++) outSpan[o++] = s;
                }
            }
        }
        return CallbackResultContinue;
    }

    [UnmanagedCallersOnly]
    static void OnError(nint stream, nint userData, int error)
    {
        var engine = (DuplexAudioEngine)GCHandle.FromIntPtr(userData).Target!;
        engine._muted = true;
        // Streams must not be closed from this callback thread; the listener hops threads.
        engine.StreamError?.Invoke(error);
    }
}
