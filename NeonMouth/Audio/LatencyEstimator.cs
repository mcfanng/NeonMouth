using NeonMouth.Audio.Dsp;

namespace NeonMouth.Audio;

/// <summary>What the phone reports about its audio path (§4.2.1). Zero means unknown.</summary>
public sealed record LatencyProbe(int SampleRate, int FramesPerBuffer, bool LowLatencyFeature, bool ProAudioFeature)
{
    public static LatencyProbe Unknown { get; } = new(0, 0, false, false);
}

/// <summary>Estimated mic-to-ear delay range in milliseconds.</summary>
public sealed record LatencyEstimate(int MinMs, int MaxMs, bool MostlyBluetooth);

/// <summary>
/// Static mic-to-ear latency estimate (§4.2.1, §4.2.3), shown before Start. The per-hop figures are
/// typical published ranges, not measurements of this phone; refine them once running-stream
/// measurements exist.
/// </summary>
public static class LatencyEstimator
{
    public static LatencyEstimate Estimate(LatencyProbe probe, AudioDevice input, AudioDevice output, VoiceEffectKind effect)
    {
        // The phone's own mic → speaker path: Android's pro-audio feature promises ≤20 ms round trip,
        // low-latency ≤45 ms; phones declaring neither are often much slower.
        var (min, max) = probe.ProAudioFeature ? (10.0, 20.0)
            : probe.LowLatencyFeature ? (20.0, 45.0)
            : (40.0, 80.0);

        // Our engine keeps two bursts in the output buffer.
        double buffer = probe.SampleRate > 0 && probe.FramesPerBuffer > 0
            ? 2.0 * probe.FramesPerBuffer * 1000 / probe.SampleRate
            : 10;
        double effectMs = VoiceEffectFactory.AddedLatencyMs(effect);
        min += buffer + effectMs;
        max += buffer + effectMs;

        var (inMin, inMax) = input switch
        {
            { IsBluetoothMic: true } => (10.0, 70.0),  // HFP/SCO call link, voice-recognition path
            { IsUsb: true } or { Kind: AudioDeviceKind.UsbMic } => (5.0, 15.0),
            _ => (0.0, 0.0),
        };
        var (outMin, outMax) = output switch
        {
            // A headset's own mic moves its output from A2DP to the call link too.
            { IsBluetooth: true } when input.IsBluetoothMic => (60.0, 150.0),
            { IsBluetooth: true } => (150.0, 250.0),  // A2DP codec + the speaker's own buffer
            { IsUsb: true } => (5.0, 15.0),           // the DAC lives in the accessory
            _ => (0.0, 0.0),
        };
        min += inMin + outMin;
        max += inMax + outMax;

        return new LatencyEstimate(RoundTo5(min), RoundTo5(max), output.IsBluetooth || input.IsBluetoothMic);
    }

    static int RoundTo5(double ms) => (int)Math.Round(ms / 5) * 5;
}
