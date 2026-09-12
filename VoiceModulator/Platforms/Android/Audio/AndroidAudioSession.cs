using System.Runtime.Versioning;
using Android.Content;
using Android.Media;
using AndroidX.Core.Content;
using VoiceModulator.Audio;
using VoiceModulator.Audio.Dsp;
using AApp = Android.App.Application;

namespace VoiceModulator.Platforms.Android.Audio;

/// <summary>
/// Android implementation of <see cref="IAudioSession"/>. Phase 1 path: built-in mic → wired
/// headphones. The phone's own speaker is never used as an output (§1.1).
/// </summary>
[SupportedOSPlatform("android31.0")]
sealed class AndroidAudioSession : IAudioSession
{
    static readonly AudioDeviceType[] WiredOutputTypes =
        [AudioDeviceType.WiredHeadphones, AudioDeviceType.WiredHeadset, AudioDeviceType.UsbHeadset];

    readonly AudioManager _audioManager;
    readonly DuplexAudioEngine _engine = new();
    readonly Lock _gate = new();
    SessionSettings _settings = new(VoiceEffectKind.Natural, 0.7f, 0.5f);
    int _outputDeviceId;

    public static AndroidAudioSession? Current { get; private set; }

    public SessionStatus Status { get; private set; } = new(SessionState.Stopped, "Stopped");
    public event EventHandler<SessionStatus>? StatusChanged;
    public event EventHandler? DevicesChanged;

    public AndroidAudioSession()
    {
        Current = this;
        _audioManager = (AudioManager)AApp.Context.GetSystemService(Context.AudioService)!;
        _audioManager.RegisterAudioDeviceCallback(new DeviceCallback(this), null);
        _engine.StreamError += OnStreamError;
    }

    public DeviceAvailability GetAvailability()
    {
        var mic = FindBuiltInMic();
        var output = FindWiredOutput();
        return new DeviceAvailability(
            mic is not null && HasMicPermission(),
            output is not null,
            mic is null ? "No microphone" : "Phone microphone",
            output is null ? "No wired headphones" : $"Wired headphones ({output.ProductName})");
    }

    public (float Input, float Output) GetLevels() =>
        _engine.IsRunning && _engine.Processor is { } p ? (p.InputPeak, p.OutputPeak) : (0f, 0f);

    public async Task<bool> RequestPermissionsAsync()
    {
        var mic = await Permissions.RequestAsync<Permissions.Microphone>();
        // Notification denial is non-fatal: the service still runs, the notification is just hidden.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        return mic == PermissionStatus.Granted;
    }

    public Task StartAsync(SessionSettings settings)
    {
        _settings = settings;
        var context = AApp.Context;
        var intent = new Intent(context, typeof(VoiceForegroundService)).SetAction(VoiceForegroundService.ActionStart);
        ContextCompat.StartForegroundService(context, intent);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        var context = AApp.Context;
        context.StartService(new Intent(context, typeof(VoiceForegroundService)).SetAction(VoiceForegroundService.ActionStop));
    }

    public void Update(SessionSettings settings)
    {
        _settings = settings;
        ApplySettings();
    }

    /// <summary>Called by the foreground service once it is in the foreground.</summary>
    internal void StartEngine()
    {
        lock (_gate)
        {
            if (_engine.IsRunning) return;
            var mic = FindBuiltInMic();
            var output = FindWiredOutput();
            if (mic is null || output is null)
            {
                SetStatus(new SessionStatus(SessionState.Error,
                    output is null ? "Connect wired headphones to continue." : "Microphone unavailable."));
                return;
            }

            try
            {
                _outputDeviceId = output.Id;
                _engine.Start(mic.Id, output.Id);
                ApplySettings();
                double latencyMs = _engine.FramesPerBurst * 2 * 1000.0 / _engine.SampleRate;
                SetStatus(new SessionStatus(SessionState.Running,
                    $"Running · {_engine.SampleRate} Hz", latencyMs));
            }
            catch (Exception ex)
            {
                SetStatus(new SessionStatus(SessionState.Error, ex.Message));
            }
        }
    }

    internal void StopEngine(SessionStatus? finalStatus = null)
    {
        lock (_gate)
        {
            _engine.Stop();
            SetStatus(finalStatus ?? new SessionStatus(SessionState.Stopped, "Stopped"));
        }
    }

    void ApplySettings()
    {
        var p = _engine.Processor;
        if (p is null) return;
        p.Effect = _settings.Effect;
        p.Intensity = _settings.Intensity;
        p.Limiter.UserGain = _settings.OutputGain;
    }

    void OnStreamError(int error)
    {
        // AAudio error callback thread: mute already happened in the engine; tear down elsewhere.
        _engine.Mute();
        Task.Run(() =>
        {
            var message = error == AAudioNative.AAUDIO_ERROR_DISCONNECTED
                ? "Audio device disconnected — output muted. Reconnect and press Start."
                : $"Audio error: {AAudioNative.ResultText(error)}";
            VoiceForegroundService.StopFromSession(new SessionStatus(SessionState.DeviceLost, message));
        });
    }

    void OnDevicesChanged(AudioDeviceInfo[]? removed)
    {
        // Mute the instant our output disappears, before AAudio's own disconnect error arrives,
        // so nothing can leak to the phone speaker (§2.3, §7).
        if (removed is not null && _engine.IsRunning && removed.Any(d => d.Id == _outputDeviceId))
            OnStreamError(AAudioNative.AAUDIO_ERROR_DISCONNECTED);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    void SetStatus(SessionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    AudioDeviceInfo? FindBuiltInMic() =>
        _audioManager.GetDevices(GetDevicesTargets.Inputs)?.FirstOrDefault(d => d.Type == AudioDeviceType.BuiltinMic);

    AudioDeviceInfo? FindWiredOutput() =>
        _audioManager.GetDevices(GetDevicesTargets.Outputs)?.FirstOrDefault(d => WiredOutputTypes.Contains(d.Type));

    static bool HasMicPermission() =>
        ContextCompat.CheckSelfPermission(AApp.Context, global::Android.Manifest.Permission.RecordAudio)
            == global::Android.Content.PM.Permission.Granted;

    sealed class DeviceCallback(AndroidAudioSession session) : AudioDeviceCallback
    {
        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) => session.OnDevicesChanged(null);
        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) => session.OnDevicesChanged(removedDevices);
    }
}
