using System.Runtime.Versioning;
using Android.Bluetooth;
using Android.Content;
using Android.Media;
using AndroidX.Core.Content;
using NeonMouth.Audio;
using NeonMouth.Audio.Dsp;
using AApp = Android.App.Application;

namespace NeonMouth.Platforms.Android.Audio;

/// <summary>
/// Android implementation of <see cref="IAudioSession"/>. Inputs: built-in or wired mic. Outputs: wired/USB
/// headphones or a Bluetooth A2DP speaker. The phone's own speaker is never used as an output (§1.1).
/// </summary>
[SupportedOSPlatform("android31.0")]
sealed class AndroidAudioSession : IAudioSession
{
    static readonly AudioDeviceType[] WiredOutputTypes =
        [AudioDeviceType.WiredHeadphones, AudioDeviceType.WiredHeadset, AudioDeviceType.UsbHeadset];
    static readonly AudioDeviceType[] WiredInputTypes = [AudioDeviceType.WiredHeadset, AudioDeviceType.UsbHeadset];

    readonly AudioManager _audioManager;
    readonly DuplexAudioEngine _engine = new();
    readonly Lock _gate = new();
    SessionSettings _settings = new(VoiceEffectKind.Natural, 0.7f, 0.5f);
    AudioDevice? _input;
    AudioDevice? _output;
    BluetoothA2dp? _a2dp;

    public static AndroidAudioSession? Current { get; private set; }

    public SessionStatus Status { get; private set; } = new(SessionState.Stopped, "Stopped");
    public event EventHandler<SessionStatus>? StatusChanged;
    public event EventHandler? DevicesChanged;

    public AndroidAudioSession()
    {
        Current = this;
        _audioManager = (AudioManager)AApp.Context.GetSystemService(Context.AudioService)!;
        _audioManager.RegisterAudioDeviceCallback(new DeviceCallback(this), null);

        // AudioManager only lists the *active* A2DP device. A speaker that is still connected but
        // lost "active" to wired headphones is found through the A2DP profile instead.
        var adapter = ((BluetoothManager?)AApp.Context.GetSystemService(Context.BluetoothService))?.Adapter;
        adapter?.GetProfileProxy(AApp.Context, new A2dpListener(this), ProfileType.A2dp);
        var filter = new IntentFilter(BluetoothA2dp.ActionConnectionStateChanged);
        ContextCompat.RegisterReceiver(AApp.Context, new A2dpReceiver(this), filter, ContextCompat.ReceiverExported);
        _engine.StreamError += OnStreamError;
    }

    /// <summary>The output the session is playing to, for the notification and FGS type.</summary>
    internal AudioDevice? CurrentOutput => _output;

    public DeviceList GetDevices()
    {
        var inputs = new List<AudioDevice>();
        var devices = _audioManager.GetDevices(GetDevicesTargets.Inputs) ?? [];
        // Phones report several built-in mics (bottom/top/back); the first is the primary one.
        if (devices.FirstOrDefault(d => d.Type == AudioDeviceType.BuiltinMic) is { } builtIn)
            inputs.Add(new AudioDevice(builtIn.Id, "builtin_mic", "Phone microphone", AudioDeviceKind.BuiltInMic));
        foreach (var d in devices.Where(d => WiredInputTypes.Contains(d.Type)))
            inputs.Add(new AudioDevice(d.Id, $"wired_mic:{d.Type}", $"Wired microphone ({d.ProductName})", AudioDeviceKind.WiredMic));

        var outputs = new List<AudioDevice>();
        foreach (var d in _audioManager.GetDevices(GetDevicesTargets.Outputs) ?? [])
        {
            if (WiredOutputTypes.Contains(d.Type))
                outputs.Add(new AudioDevice(d.Id, $"wired_out:{d.Type}", $"Wired headphones ({d.ProductName})", AudioDeviceKind.WiredOutput));
            else if (d.Type == AudioDeviceType.BluetoothA2dp)
            {
                string key = string.IsNullOrEmpty(d.Address) ? d.ProductName ?? "" : d.Address;
                outputs.Add(new AudioDevice(d.Id, $"bt:{key}", $"Bluetooth: {d.ProductName}", AudioDeviceKind.BluetoothSpeaker));
            }
        }
        AddInactiveBluetoothSpeakers(outputs);
        return new DeviceList(inputs, outputs, HasPermission(global::Android.Manifest.Permission.RecordAudio));
    }

    void AddInactiveBluetoothSpeakers(List<AudioDevice> outputs)
    {
        if (_a2dp is null || !HasPermission(global::Android.Manifest.Permission.BluetoothConnect)) return;
        IList<BluetoothDevice>? connected;
        try { connected = _a2dp.ConnectedDevices; }
        catch (Java.Lang.SecurityException) { return; }

        foreach (var bt in connected ?? [])
        {
            string key = $"bt:{bt.Address}";
            if (outputs.Any(o => o.Key == key)) continue;
            // Negative IDs never collide with AudioDeviceInfo IDs; stable for the process lifetime.
            int id = -1 - (key.GetHashCode() & 0x7FFFFFFF) % 1_000_000;
            outputs.Add(new AudioDevice(id, key, $"Bluetooth: {bt.Name} (not active)",
                AudioDeviceKind.BluetoothSpeaker, NeedsActivation: true));
        }
    }

    public void RequestActivation(AudioDevice device)
    {
        // The media output panel lets the user pick the active output in one tap. It isn't a public
        // API and varies by Android version, so fall back to Bluetooth settings (tapping a connected
        // device there makes it active).
        var context = AApp.Context;
        try
        {
            context.StartActivity(new Intent("com.android.settings.panel.action.MEDIA_OUTPUT")
                .PutExtra("com.android.settings.panel.extra.PACKAGE_NAME", context.PackageName)
                .AddFlags(ActivityFlags.NewTask));
        }
        catch (ActivityNotFoundException)
        {
            context.StartActivity(new Intent(global::Android.Provider.Settings.ActionBluetoothSettings)
                .AddFlags(ActivityFlags.NewTask));
        }
    }

    public (float Input, float Output) GetLevels() =>
        _engine.IsRunning && _engine.Processor is { } p ? (p.InputPeak, p.OutputPeak) : (0f, 0f);

    public async Task<bool> RequestPermissionsAsync()
    {
        var mic = await Permissions.RequestAsync<Permissions.Microphone>();
        // Bluetooth denial is non-fatal: A2DP output still works, but the device address (used to
        // remember the last speaker) may be hidden and the connectedDevice FGS type is skipped.
        await Permissions.RequestAsync<BluetoothConnectPermission>();
        // Notification denial is non-fatal: the service still runs, the notification is just hidden.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        return mic == PermissionStatus.Granted;
    }

    public Task StartAsync(SessionSettings settings, AudioDevice input, AudioDevice output)
    {
        _settings = settings;
        _input = input;
        _output = output;
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

    public void SwitchDevices(AudioDevice input, AudioDevice output)
    {
        lock (_gate)
        {
            _input = input;
            _output = output;
            if (!_engine.IsRunning) return;
            _engine.Stop();
            StartEngine();
            if (Status.State != SessionState.Running)
            {
                var failed = Status;
                Task.Run(() => VoiceForegroundService.StopFromSession(failed));
            }
        }
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
            // Re-check: the selection may have been unplugged between Start and the service coming up.
            var devices = GetDevices();
            var input = devices.Inputs.FirstOrDefault(d => d.Id == _input?.Id);
            var output = devices.Outputs.FirstOrDefault(d => d.Id == _output?.Id);
            if (input is null || output is null)
            {
                SetStatus(new SessionStatus(SessionState.Error, output is null
                    ? "Selected output is no longer connected."
                    : "Selected microphone is no longer connected."));
                return;
            }

            try
            {
                // AAudio's device ID is the preferred-device override (§5.3): a wired headset mic
                // can't take over input from the selected built-in mic.
                _engine.Start(input.Id, output.Id);
                ApplySettings();
                double latencyMs = _engine.FramesPerBurst * 2 * 1000.0 / _engine.SampleRate;
                SetStatus(new SessionStatus(SessionState.Running,
                    $"Running · {output.Name} · {_engine.SampleRate} Hz", latencyMs));
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
        global::Android.Util.Log.Info("NeonMouth", $"ApplySettings effect={_settings.Effect}");
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
        if (removed is not null && _engine.IsRunning && removed.Any(d => d.Id == _output?.Id || d.Id == _input?.Id))
            OnStreamError(AAudioNative.AAUDIO_ERROR_DISCONNECTED);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    void SetStatus(SessionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    internal static bool HasPermission(string permission) =>
        ContextCompat.CheckSelfPermission(AApp.Context, permission) == global::Android.Content.PM.Permission.Granted;

    sealed class BluetoothConnectPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            [(global::Android.Manifest.Permission.BluetoothConnect, true)];
    }

    sealed class A2dpListener(AndroidAudioSession session) : Java.Lang.Object, IBluetoothProfileServiceListener
    {
        public void OnServiceConnected(ProfileType profile, IBluetoothProfile? proxy)
        {
            session._a2dp = proxy as BluetoothA2dp;
            session.DevicesChanged?.Invoke(session, EventArgs.Empty);
        }

        public void OnServiceDisconnected(ProfileType profile) => session._a2dp = null;
    }

    /// <summary>An inactive speaker connecting/disconnecting doesn't trigger AudioDeviceCallback.</summary>
    sealed class A2dpReceiver(AndroidAudioSession session) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) => session.OnDevicesChanged(null);
    }

    sealed class DeviceCallback(AndroidAudioSession session) : AudioDeviceCallback
    {
        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) => session.OnDevicesChanged(null);
        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) => session.OnDevicesChanged(removedDevices);
    }
}
