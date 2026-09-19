using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using AndroidX.Core.View;

namespace NeonMouth
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            ApplyStatusBarAppearance(Resources?.Configuration);
        }

        // UiMode is in ConfigurationChanges, so the activity isn't recreated on a theme switch.
        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            ApplyStatusBarAppearance(newConfig);
        }

        // Dark status bar icons on the light theme, light icons on the dark theme.
        void ApplyStatusBarAppearance(Configuration? config)
        {
            if (Window is null || config is null)
                return;
            var night = (config.UiMode & UiMode.NightMask) == UiMode.NightYes;
            // colors.xml / values-night only apply at launch, so repaint the bars on a live switch.
            var bar = Android.Graphics.Color.ParseColor(night ? "#0B0B14" : "#FFFFFF");
            Window.SetStatusBarColor(bar);
            Window.SetNavigationBarColor(bar);
            // On Android 15+ (edge-to-edge) those calls are ignored and the area behind the status bar is
            // Shell's AppBarLayout, whose background was set from colorPrimary at launch.
            Window.DecorView.Post(() => RepaintAppBar(Window.DecorView, bar));
            var controller = WindowCompat.GetInsetsController(Window, Window.DecorView);
            controller.AppearanceLightStatusBars = !night;
            controller.AppearanceLightNavigationBars = !night;
        }

        static void RepaintAppBar(Android.Views.View view, Android.Graphics.Color color)
        {
            if (view is Google.Android.Material.AppBar.AppBarLayout appBar)
                appBar.SetBackgroundColor(color);
            if (view is Android.Views.ViewGroup group)
                for (var i = 0; i < group.ChildCount; i++)
                    if (group.GetChildAt(i) is { } child)
                        RepaintAppBar(child, color);
        }
    }
}
