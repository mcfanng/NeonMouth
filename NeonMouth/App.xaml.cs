namespace NeonMouth;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        if (OperatingSystem.IsAndroid() && !OperatingSystem.IsAndroidVersionAtLeast(31))
            return new Window(new UnsupportedPage());
        return new Window(new AppShell());
    }
}
