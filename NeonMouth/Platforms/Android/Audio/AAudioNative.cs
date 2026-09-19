using System.Runtime.InteropServices;

namespace NeonMouth.Platforms.Android.Audio;

/// <summary>P/Invoke surface for the NDK AAudio C API (libaaudio.so). Only what the app uses.</summary>
static unsafe partial class AAudioNative
{
    const string Lib = "libaaudio.so";

    public const int AAUDIO_OK = 0;
    public const int AAUDIO_ERROR_DISCONNECTED = -899;

    public const int DirectionOutput = 0;
    public const int DirectionInput = 1;

    public const int FormatPcmFloat = 2;

    public const int SharingModeExclusive = 0;
    public const int SharingModeShared = 1;

    public const int PerformanceModeLowLatency = 12;

    public const int UsageMedia = 1;
    public const int ContentTypeSpeech = 1;
    public const int InputPresetVoicePerformance = 10;

    public const int CallbackResultContinue = 0;
    public const int CallbackResultStop = 1;

    [LibraryImport(Lib)] public static partial int AAudio_createStreamBuilder(out nint builder);
    [LibraryImport(Lib)] public static partial int AAudioStreamBuilder_openStream(nint builder, out nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStreamBuilder_delete(nint builder);

    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setDeviceId(nint builder, int deviceId);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setDirection(nint builder, int direction);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setSharingMode(nint builder, int mode);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setSampleRate(nint builder, int sampleRate);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setChannelCount(nint builder, int channelCount);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setFormat(nint builder, int format);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setPerformanceMode(nint builder, int mode);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setUsage(nint builder, int usage);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setContentType(nint builder, int contentType);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setInputPreset(nint builder, int preset);

    [LibraryImport(Lib)]
    public static partial void AAudioStreamBuilder_setDataCallback(
        nint builder, delegate* unmanaged<nint, nint, nint, int, int> callback, nint userData);

    [LibraryImport(Lib)]
    public static partial void AAudioStreamBuilder_setErrorCallback(
        nint builder, delegate* unmanaged<nint, nint, int, void> callback, nint userData);

    [LibraryImport(Lib)] public static partial int AAudioStream_requestStart(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_requestStop(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_close(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_read(nint stream, float* buffer, int numFrames, long timeoutNanoseconds);
    [LibraryImport(Lib)] public static partial int AAudioStream_getSampleRate(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_getChannelCount(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_getFramesPerBurst(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_setBufferSizeInFrames(nint stream, int numFrames);
    [LibraryImport(Lib)] public static partial int AAudioStream_getPerformanceMode(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_getSharingMode(nint stream);

    [LibraryImport(Lib)] public static partial nint AAudio_convertResultToText(int result);

    public static string ResultText(int result) =>
        Marshal.PtrToStringAnsi(AAudio_convertResultToText(result)) ?? result.ToString();
}
