using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NeonMouth.Audio;
using NeonMouth.Audio.Dsp;

namespace NeonMouth.ViewModels;

public partial class MainViewModel : ObservableObject
{
    readonly IAudioSession _session;
    DeviceList _devices = new([], [], false);
    bool _hasScanned;
    bool _permissionsRequested;
    bool _rebuildingLists;
    bool _activationPrompting;
    /// <summary>Key of a speaker the user chose to reactivate; selected as soon as it's active.</summary>
    string? _awaitingActivationKey;

    public IReadOnlyList<VoiceEffectKind> Effects { get; } = Enum.GetValues<VoiceEffectKind>();

    [ObservableProperty] VoiceEffectKind _selectedEffect;
    [ObservableProperty] double _intensity;
    [ObservableProperty] double _outputGain;

    // Pickers bind to name lists + SelectedIndex; SelectedItem binding proved unreliable (see Effects).
    [ObservableProperty] IReadOnlyList<string> _inputNames = [];
    [ObservableProperty] IReadOnlyList<string> _outputNames = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedInputIndex), nameof(IsRecommendedSetup))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    AudioDevice? _selectedInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedOutputIndex), nameof(IsRecommendedSetup))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    AudioDevice? _selectedOutput;

    [ObservableProperty] bool _inputAutoSelected;
    [ObservableProperty] bool _outputAutoSelected;

    /// <summary>A device that appeared after the initial scan, offered via "switch?" (§2.5).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingDevice), nameof(PendingDeviceText))]
    AudioDevice? _pendingDevice;

    public bool HasPendingDevice => PendingDevice is not null;

    public string PendingDeviceText => $"New output available: {PendingDevice?.Name}. Switch output to it?";

    public bool IsRecommendedSetup =>
        SelectedInput?.Kind == AudioDeviceKind.BuiltInMic && SelectedOutput?.Kind == AudioDeviceKind.WiredOutput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    bool _isRunning;

    [ObservableProperty] string _statusText = "Stopped";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockingMessage))]
    string? _blockingMessage;

    public bool HasBlockingMessage => BlockingMessage is not null;
    [ObservableProperty] bool _showDisclosure;

    [ObservableProperty] double _inputLevel;
    [ObservableProperty] double _outputLevel;

    public string StartStopText => IsRunning ? "Stop" : "Start";

    public MainViewModel(IAudioSession session)
    {
        _session = session;
        SelectedEffect = Enum.TryParse<VoiceEffectKind>(Preferences.Default.Get(SettingsKeys.Effect, ""), out var e) ? e : VoiceEffectKind.Witch;
        Intensity = Preferences.Default.Get(SettingsKeys.Intensity, 0.7);
        OutputGain = Preferences.Default.Get(SettingsKeys.OutputGain, 0.5);
        ShowDisclosure = !Preferences.Default.Get(SettingsKeys.DisclosureAccepted, false);

        _session.StatusChanged += (_, s) => MainThread.BeginInvokeOnMainThread(() => ApplyStatus(s));
        _session.DevicesChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Rescan);
        ApplyStatus(_session.Status);
        Rescan();

        // Level meters: poll a few times a second rather than pushing from the audio thread.
        Application.Current?.Dispatcher.StartTimer(TimeSpan.FromMilliseconds(100), () =>
        {
            var (input, output) = _session.GetLevels();
            InputLevel = input;
            OutputLevel = output;
            return true;
        });
    }

    SessionSettings CurrentSettings => new(SelectedEffect, (float)Intensity, (float)OutputGain);

    // Picker binds by index: SelectedItem-to-enum binding silently failed to update SelectedEffect.
    public int SelectedEffectIndex
    {
        get => (int)SelectedEffect;
        set
        {
            if (value >= 0 && value < Effects.Count) SelectedEffect = Effects[value];
        }
    }

    public int SelectedInputIndex
    {
        get => IndexOf(_devices.Inputs, SelectedInput);
        set
        {
            // Replacing the ItemsSource makes the Picker push -1 / stale indexes; ignore those.
            if (_rebuildingLists || value < 0 || value >= _devices.Inputs.Count || _devices.Inputs[value] == SelectedInput) return;
            SelectedInput = _devices.Inputs[value];
            InputAutoSelected = false;
            ApplyDeviceChoice();
        }
    }

    /// <summary>
    /// Bound one-way: the page reports user picks via <see cref="TrySelectOutput"/>. A two-way binding
    /// ping-ponged forever (32k+ writes, ANR) when an inactive speaker was picked and snapped back.
    /// </summary>
    public int SelectedOutputIndex => IndexOf(_devices.Outputs, SelectedOutput);

    public void RefreshOutputSelection() => OnPropertyChanged(nameof(SelectedOutputIndex));

    /// <summary>
    /// Handles a user pick in the Output Picker. Returns false when the Picker must be reset to
    /// <see cref="SelectedOutputIndex"/> (an inactive speaker was picked; the user is asked instead).
    /// </summary>
    public bool TrySelectOutput(int index)
    {
        if (_rebuildingLists || index < 0 || index >= _devices.Outputs.Count || _devices.Outputs[index] == SelectedOutput)
            return true;
        if (_devices.Outputs[index] is { NeedsActivation: true } inactive)
        {
            _ = PromptActivationAsync(inactive);
            return false;
        }
        SelectedOutput = _devices.Outputs[index];
        OutputAutoSelected = false;
        ApplyDeviceChoice();
        return true;
    }

    partial void OnSelectedEffectChanged(VoiceEffectKind value)
    {
        OnPropertyChanged(nameof(SelectedEffectIndex));
        Preferences.Default.Set(SettingsKeys.Effect, value.ToString());
        _session?.Update(CurrentSettings);
    }

    partial void OnIntensityChanged(double value)
    {
        Preferences.Default.Set(SettingsKeys.Intensity, value);
        _session?.Update(CurrentSettings);
    }

    partial void OnOutputGainChanged(double value)
    {
        Preferences.Default.Set(SettingsKeys.OutputGain, value);
        _session?.Update(CurrentSettings);
    }

    [RelayCommand]
    async Task AcceptDisclosureAsync()
    {
        Preferences.Default.Set(SettingsKeys.DisclosureAccepted, true);
        ShowDisclosure = false;
        await RequestPermissionsOnceAsync();
    }

    /// <summary>
    /// Asks for mic + Bluetooth permissions up front (after the disclosure), once per app run.
    /// Without BLUETOOTH_CONNECT the app can't see connected-but-inactive speakers, so waiting
    /// until Start would leave the Output list incomplete.
    /// </summary>
    public async Task RequestPermissionsOnceAsync()
    {
        if (_permissionsRequested || ShowDisclosure) return;
        _permissionsRequested = true;
        await _session.RequestPermissionsAsync();
        Rescan();
    }

    [RelayCommand]
    void Rescan()
    {
        var previous = _devices;
        _devices = _session.GetDevices();

        // Keep a still-connected selection (IDs change on reconnect, so match by ID first).
        var input = _devices.Inputs.FirstOrDefault(d => d.Id == SelectedInput?.Id);
        var output = _devices.Outputs.FirstOrDefault(d => d.Id == SelectedOutput?.Id);

        // Re-run auto-selection when the selection is gone, or was automatic and we're idle
        // (e.g. plugging in headphones while stopped upgrades a Bluetooth default to wired).
        bool autoInput = input is null || (InputAutoSelected && !IsRunning);
        bool autoOutput = output is null || (OutputAutoSelected && !IsRunning);
        if (autoInput) input = AutoSelectInput(_devices.Inputs);
        if (autoOutput) output = AutoSelectOutput(_devices.Outputs);

        // The Picker pushes stale indexes back while its list is replaced; on resume from Bluetooth
        // settings that re-triggered activation in a loop, so ignore index writes until done.
        _rebuildingLists = true;
        InputNames = _devices.Inputs.Select(d => d.Name).ToList();
        OutputNames = _devices.Outputs.Select(d => d.Name).ToList();
        SelectedInput = input;
        SelectedOutput = output;
        InputAutoSelected = autoInput && input is not null;
        OutputAutoSelected = autoOutput && output is not null;
        // Re-push indexes after the name lists changed so the Pickers show the selection.
        OnPropertyChanged(nameof(SelectedInputIndex));
        OnPropertyChanged(nameof(SelectedOutputIndex));
        _rebuildingLists = false;

        if (PendingDevice is { } pending && !_devices.Inputs.Concat(_devices.Outputs).Any(d => d.Id == pending.Id))
            PendingDevice = null;
        if (_hasScanned)
            OfferNewDevice(previous);
        _hasScanned = true;

        BlockingMessage = (input, output, _devices.HasMicPermission) switch
        {
            (_, null, _) when _devices.Outputs.Any(d => d.NeedsActivation) =>
                "Your Bluetooth speaker is connected but not active. Choose it under Output to switch back to it.",
            (_, null, _) => "Connect wired headphones or pair a Bluetooth speaker to continue, then tap Rescan.",
            (null, _, _) => "No microphone is available.",
            (_, _, false) => "Microphone permission is needed. Press Start to grant it, or enable it in system Settings.",
            _ => null,
        };
    }

    /// <summary>
    /// Reacts to a newly connected output. Wired headphones are switched to automatically, even
    /// mid-session: it's the safest, lowest-latency output and matches Android's own routing. A new
    /// Bluetooth speaker raises feedback risk, so it only gets a non-blocking "switch?" prompt (§2.5).
    /// New mics are never offered: a one-tap switch to e.g. a speaker's built-in mic risks feedback,
    /// so input changes stay a deliberate manual choice in the Input picker.
    /// </summary>
    void OfferNewDevice(DeviceList previous)
    {
        // The user already chose this speaker when activating it, so select it without asking.
        if (_awaitingActivationKey is { } key
            && _devices.Outputs.FirstOrDefault(d => d.Key == key && !d.NeedsActivation) is { } activated)
        {
            _awaitingActivationKey = null;
            PendingDevice = null;
            SelectedOutput = activated;
            OutputAutoSelected = false;
            ApplyDeviceChoice();
            return;
        }

        var added = _devices.Outputs
            .FirstOrDefault(d => d != SelectedOutput && !d.NeedsActivation && !previous.Outputs.Any(p => p.Id == d.Id));
        if (added is null) return;

        if (added.Kind == AudioDeviceKind.WiredOutput)
        {
            PendingDevice = null;
            SelectedOutput = added;
            OutputAutoSelected = true;
            ApplyDeviceChoice();
        }
        else
        {
            PendingDevice = added;
        }
    }

    [RelayCommand]
    void AcceptPendingDevice()
    {
        if (PendingDevice is not { } device) return;
        PendingDevice = null;
        SelectedOutput = device;
        OutputAutoSelected = false;
        ApplyDeviceChoice();
    }

    [RelayCommand]
    void DismissPendingDevice() => PendingDevice = null;

    /// <summary>
    /// Sends the user to Bluetooth settings to reactivate a speaker. The first time for a given
    /// speaker it explains why first; speakers seen before go straight to settings.
    /// </summary>
    async Task PromptActivationAsync(AudioDevice device)
    {
        if (IsKnownBluetoothOutput(device.Key))
        {
            StartActivation(device);
            return;
        }
        if (_activationPrompting || Application.Current?.Windows.FirstOrDefault()?.Page is not { } page) return;
        _activationPrompting = true;
        try
        {
            string name = device.Name.Replace("Bluetooth: ", "").Replace(" (not active)", "");
            bool open = await page.DisplayAlertAsync("Switch to " + name + "?",
                $"Android is no longer sending audio to {name}. Open Bluetooth settings and tap {name} to switch back to it, then return here.",
                "Open", "Cancel");
            if (open) StartActivation(device);
        }
        finally
        {
            _activationPrompting = false;
        }
    }

    void ApplyDeviceChoice()
    {
        if (!IsRunning || SelectedInput is null || SelectedOutput is null) return;
        RememberOutput(SelectedOutput);
        _session.SwitchDevices(SelectedInput, SelectedOutput);
    }

    /// <summary>Start is disabled without both devices (§3.1); a missing mic permission is requested on Start.</summary>
    bool CanToggle() => IsRunning || (SelectedInput is not null && SelectedOutput is not null);

    [RelayCommand(CanExecute = nameof(CanToggle))]
    async Task ToggleAsync()
    {
        if (IsRunning)
        {
            _session.Stop();
            return;
        }

        if (ShowDisclosure) return;
        if (!await _session.RequestPermissionsAsync())
        {
            BlockingMessage = "Microphone permission was denied. Enable it in system Settings to use the app.";
            return;
        }

        Rescan();
        if (HasBlockingMessage || SelectedInput is null || SelectedOutput is null) return;
        PendingDevice = null;
        RememberOutput(SelectedOutput);
        await _session.StartAsync(CurrentSettings, SelectedInput, SelectedOutput);
    }

    static AudioDevice? AutoSelectInput(IReadOnlyList<AudioDevice> inputs) =>
        // Phase 3 adds "last-used Bluetooth mic" ahead of the built-in mic (§2.5).
        inputs.FirstOrDefault(d => d.Kind == AudioDeviceKind.BuiltInMic) ?? inputs.FirstOrDefault();

    static AudioDevice? AutoSelectOutput(IReadOnlyList<AudioDevice> outputs)
    {
        // §2.5: wired → last-used Bluetooth speaker. A connected but never-used speaker is still
        // offered as a last resort rather than reporting "no valid output".
        string lastBt = Preferences.Default.Get(SettingsKeys.LastBluetoothOutput, "");
        outputs = outputs.Where(d => !d.NeedsActivation).ToList();
        return outputs.FirstOrDefault(d => d.Kind == AudioDeviceKind.WiredOutput)
            ?? outputs.FirstOrDefault(d => d.IsBluetooth && d.Key == lastBt)
            ?? outputs.FirstOrDefault(d => d.IsBluetooth);
    }

    void StartActivation(AudioDevice device)
    {
        MarkKnownBluetoothOutput(device.Key);
        _awaitingActivationKey = device.Key;
        _session.RequestActivation(device);
    }

    static void RememberOutput(AudioDevice output)
    {
        if (!output.IsBluetooth) return;
        Preferences.Default.Set(SettingsKeys.LastBluetoothOutput, output.Key);
        MarkKnownBluetoothOutput(output.Key);
    }

    static string[] KnownBluetoothOutputs() =>
        Preferences.Default.Get(SettingsKeys.KnownBluetoothOutputs, "").Split('|', StringSplitOptions.RemoveEmptyEntries);

    static bool IsKnownBluetoothOutput(string key) => KnownBluetoothOutputs().Contains(key);

    static void MarkKnownBluetoothOutput(string key)
    {
        if (!IsKnownBluetoothOutput(key))
            Preferences.Default.Set(SettingsKeys.KnownBluetoothOutputs, string.Join('|', KnownBluetoothOutputs().Append(key)));
    }

    static int IndexOf(IReadOnlyList<AudioDevice> list, AudioDevice? device)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == device) return i;
        return -1;
    }

    void ApplyStatus(SessionStatus status)
    {
        IsRunning = status.State == SessionState.Running;
        StatusText = status.OutputLatencyMs is { } ms
            ? $"{status.Message} · ~{ms:0} ms output buffer"
            : status.Message;
    }
}
