using NeonMouth.Audio;

namespace NeonMouth.Services;

/// <summary>Stub for platforms (iOS) and Android versions below 12 where the engine isn't available.</summary>
sealed class UnsupportedAudioSession : IAudioSession
{
    public SessionStatus Status { get; } = new(SessionState.Error, "Not supported on this platform.");
    public event EventHandler<SessionStatus>? StatusChanged { add { } remove { } }
    public event EventHandler? DevicesChanged { add { } remove { } }

    public DeviceList GetDevices() => new([], [], false);
    public (float Input, float Output) GetLevels() => (0, 0);
    public Task<bool> RequestPermissionsAsync() => Task.FromResult(false);
    public Task StartAsync(SessionSettings settings, AudioDevice input, AudioDevice output) => Task.CompletedTask;
    public void SwitchDevices(AudioDevice input, AudioDevice output) { }
    public void RequestActivation(AudioDevice device) { }
    public void Stop() { }
    public void Update(SessionSettings settings) { }
}
