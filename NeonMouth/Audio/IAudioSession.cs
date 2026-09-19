using NeonMouth.Audio.Dsp;

namespace NeonMouth.Audio;

public enum SessionState
{
    Stopped,
    Running,
    /// <summary>Output was muted because a device disconnected (§7).</summary>
    DeviceLost,
    Error,
}

public enum AudioDeviceKind
{
    BuiltInMic,
    WiredMic,
    /// <summary>Bluetooth headset mic (HFP/SCO or LE Audio). Never auto-selected.</summary>
    BluetoothMic,
    WiredOutput,
    BluetoothSpeaker,
}

/// <summary>
/// A selectable input or output. <see cref="Id"/> is only valid while connected; <see cref="Key"/>
/// is stable across reconnects and is what gets persisted (e.g. last-used Bluetooth speaker).
/// <see cref="NeedsActivation"/> marks a Bluetooth speaker that is connected but not the OS's active
/// audio device (e.g. after wired headphones took over); it can't be opened until reactivated.
/// </summary>
public sealed record AudioDevice(int Id, string Key, string Name, AudioDeviceKind Kind, bool NeedsActivation = false)
{
    public bool IsBluetooth => Kind == AudioDeviceKind.BluetoothSpeaker;
}

/// <summary>Currently connected candidates. The phone's own speaker is never in <see cref="Outputs"/> (§1.1).</summary>
public sealed record DeviceList(IReadOnlyList<AudioDevice> Inputs, IReadOnlyList<AudioDevice> Outputs, bool HasMicPermission);

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

    DeviceList GetDevices();

    /// <summary>Current input/output peak levels (0..1) while running; zeros otherwise.</summary>
    (float Input, float Output) GetLevels();

    Task<bool> RequestPermissionsAsync();

    Task StartAsync(SessionSettings settings, AudioDevice input, AudioDevice output);

    /// <summary>
    /// Opens the OS UI where the user can make an inactive device (<see cref="AudioDevice.NeedsActivation"/>)
    /// active again; apps can't do that themselves. <see cref="DevicesChanged"/> fires once it is.
    /// </summary>
    void RequestActivation(AudioDevice device);

    /// <summary>Moves a running session to different devices (restarts the streams).</summary>
    void SwitchDevices(AudioDevice input, AudioDevice output);

    void Stop();

    /// <summary>Applies effect/intensity/gain live without restarting.</summary>
    void Update(SessionSettings settings);
}
