using Microsoft.Extensions.Logging;
using NeonMouth.Audio;
using NeonMouth.ViewModels;

namespace NeonMouth;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<IAudioSession>(_ => CreateAudioSession());
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient<MainPage>();

#if ANDROID
        // No system touch clicks on the app's controls: the mic can pick them up mid-session.
        Microsoft.Maui.Handlers.ViewHandler.ViewMapper.AppendToMapping("NoTouchSounds",
            (handler, _) =>
            {
                if (handler.PlatformView is Android.Views.View view) view.SoundEffectsEnabled = false;
            });

        // Give the level meters a visible track. Hooked to ProgressColor because that mapping
        // re-runs when the app theme changes (its style uses AppThemeBinding).
        Microsoft.Maui.Handlers.ProgressBarHandler.Mapper.AppendToMapping(nameof(IProgress.ProgressColor),
            (handler, _) =>
            {
                var key = Application.Current?.RequestedTheme == AppTheme.Dark ? "Gray500" : "Gray300";
                if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color track)
                    handler.PlatformView.ProgressBackgroundTintList =
                        Android.Content.Res.ColorStateList.ValueOf(Microsoft.Maui.Platform.ColorExtensions.ToPlatform(track));
            });
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    static IAudioSession CreateAudioSession()
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            return new Platforms.Android.Audio.AndroidAudioSession();
#endif
        return new Services.UnsupportedAudioSession();
    }
}
