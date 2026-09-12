using VoiceModulator.Audio.Dsp;

namespace VoiceModulator.Audio;

public enum SessionState
{
    Stopped,
    Running,
    /// <summary>Output was muted because a device disconnected (§7).</summary>
    DeviceLost,
    Error,
}

public sealed record DeviceAvailability(bool HasInput, bool HasOutput, string InputName, string OutputName);

public sealed record SessionSettings(VoiceEffectKind Effect, float Intensity, float OutputGain);

public sealed record SessionStatus(SessionState State, string Message, double? OutputLatencyMs = null);

/// <summary>Platform audio session: device discovery, foreground service and the real-time engine.</summary>
public interface IAudioSession
{
    SessionStatus Status { get; }

    /// <summary>Raised on an arbitrary thread when <see cref="Status"/> changes.</summary>
    event EventHandler<SessionStatus>? StatusChanged;

    /// <summary>Raised on an arbitrary thread when audio devices are added or removed.</summary>
    event EventHandler? DevicesChanged;

    DeviceAvailability GetAvailability();

    Task<bool> RequestPermissionsAsync();

    Task StartAsync(SessionSettings settings);

    void Stop();

    /// <summary>Applies effect/intensity/gain live without restarting.</summary>
    void Update(SessionSettings settings);
}
