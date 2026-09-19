using VoiceModulator.Audio;

namespace VoiceModulator.Services;

/// <summary>Stub for platforms (iOS) and Android versions below 12 where the engine isn't available.</summary>
sealed class UnsupportedAudioSession : IAudioSession
{
    public SessionStatus Status { get; } = new(SessionState.Error, "Not supported on this platform.");
    public event EventHandler<SessionStatus>? StatusChanged { add { } remove { } }
    public event EventHandler? DevicesChanged { add { } remove { } }

    public DeviceAvailability GetAvailability() => new(false, false, "Unavailable", "Unavailable");
    public (float Input, float Output) GetLevels() => (0, 0);
    public Task<bool> RequestPermissionsAsync() => Task.FromResult(false);
    public Task StartAsync(SessionSettings settings) => Task.CompletedTask;
    public void Stop() { }
    public void Update(SessionSettings settings) { }
}
