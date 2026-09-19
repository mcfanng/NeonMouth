namespace NeonMouth;

/// <summary>Shown instead of the app on Android versions below 12 (API 31).</summary>
public sealed class UnsupportedPage : ContentPage
{
    public UnsupportedPage()
    {
        Content = new VerticalStackLayout
        {
            Padding = 32,
            Spacing = 12,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = "Android 12 or newer required", FontSize = 22, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center },
                new Label { Text = "NeonMouth needs the low-latency audio features introduced in Android 12. Please update your device to use this app.", HorizontalTextAlignment = TextAlignment.Center },
            },
        };
    }
}
