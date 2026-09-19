using VoiceModulator.ViewModels;

namespace VoiceModulator;

public partial class MainPage : ContentPage
{
    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Re-scan on resume: devices may have changed while the app wasn't running (§2.5).
        ((MainViewModel)BindingContext).RescanCommand.Execute(null);
    }
}
