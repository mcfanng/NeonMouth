using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceModulator.Audio;
using VoiceModulator.Audio.Dsp;

namespace VoiceModulator.ViewModels;

public partial class MainViewModel : ObservableObject
{
    readonly IAudioSession _session;

    public IReadOnlyList<VoiceEffectKind> Effects { get; } = Enum.GetValues<VoiceEffectKind>();

    [ObservableProperty] VoiceEffectKind _selectedEffect;
    [ObservableProperty] double _intensity;
    [ObservableProperty] double _outputGain;

    [ObservableProperty] string _inputName = "";
    [ObservableProperty] string _outputName = "";
    [ObservableProperty] bool _hasInput;
    [ObservableProperty] bool _hasOutput;

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

    partial void OnSelectedEffectChanged(VoiceEffectKind value)
    {
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
    void AcceptDisclosure()
    {
        Preferences.Default.Set(SettingsKeys.DisclosureAccepted, true);
        ShowDisclosure = false;
    }

    [RelayCommand]
    void Rescan()
    {
        var a = _session.GetAvailability();
        InputName = a.InputName;
        OutputName = a.OutputName;
        HasInput = a.HasInput;
        HasOutput = a.HasOutput;
        BlockingMessage = (a.HasInput, a.HasOutput) switch
        {
            (_, false) => "Connect wired headphones to continue.",
            (false, _) => "Microphone permission is needed. Press Start to grant it, or enable it in system Settings.",
            _ => null,
        };
    }

    [RelayCommand]
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
        if (!HasInput || !HasOutput) return;
        await _session.StartAsync(CurrentSettings);
    }

    void ApplyStatus(SessionStatus status)
    {
        IsRunning = status.State == SessionState.Running;
        StatusText = status.OutputLatencyMs is { } ms
            ? $"{status.Message} · ~{ms:0} ms output buffer"
            : status.Message;
    }
}
