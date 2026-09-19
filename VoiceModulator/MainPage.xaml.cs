using VoiceModulator.ViewModels;

namespace VoiceModulator;

public partial class MainPage : ContentPage
{
    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    bool _resettingOutputPicker;

    void OnOutputPickerChanged(object? sender, EventArgs e)
    {
        if (_resettingOutputPicker) return;
        var viewModel = (MainViewModel)BindingContext;
        if (viewModel.TrySelectOutput(OutputPicker.SelectedIndex)) return;

        // Put the Picker back on the real selection once, after this event has finished. Goes through
        // the one-way binding: assigning SelectedIndex directly would detach the binding.
        Dispatcher.Dispatch(() =>
        {
            _resettingOutputPicker = true;
            viewModel.RefreshOutputSelection();
            _resettingOutputPicker = false;
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var viewModel = (MainViewModel)BindingContext;
        // Re-scan on resume: devices may have changed while the app wasn't running (§2.5).
        viewModel.RescanCommand.Execute(null);
        await viewModel.RequestPermissionsOnceAsync();
    }
}
