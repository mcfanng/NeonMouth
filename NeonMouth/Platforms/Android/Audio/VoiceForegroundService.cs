using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using NeonMouth.Audio;
using AApp = Android.App.Application;
using SessionStatus = NeonMouth.Audio.SessionStatus;

namespace NeonMouth.Platforms.Android.Audio;

/// <summary>Keeps capture/processing alive with the screen off (§2.4, §3.3).</summary>
[SupportedOSPlatform("android31.0")]
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMicrophone | ForegroundService.TypeConnectedDevice)]
sealed class VoiceForegroundService : Service
{
    public const string ActionStart = "com.mcfanng.neonmouth.START";
    public const string ActionStop = "com.mcfanng.neonmouth.STOP";
    const string ChannelId = "voice_session";
    const int NotificationId = 1001;

    static VoiceForegroundService? _instance;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var session = AndroidAudioSession.Current;
        if (intent?.Action == ActionStop || session is null)
        {
            Shutdown(null);
            return StartCommandResult.NotSticky;
        }

        _instance = this;
        EnsureChannel();
        StartForeground(NotificationId, BuildNotification(session.Status), ForegroundTypes(session));

        session.StatusChanged -= OnStatusChanged;
        session.StatusChanged += OnStatusChanged;
        session.StartEngine();

        if (session.Status.State != SessionState.Running) Shutdown(session.Status);
        return StartCommandResult.NotSticky;
    }

    /// <summary>
    /// connectedDevice applies to a Bluetooth output, but from API 34 it throws unless a Bluetooth
    /// permission is granted, so it's only added when it can be.
    /// </summary>
    static ForegroundService ForegroundTypes(AndroidAudioSession session) =>
        session.CurrentOutput?.IsBluetooth == true
            && AndroidAudioSession.HasPermission(global::Android.Manifest.Permission.BluetoothConnect)
            ? ForegroundService.TypeMicrophone | ForegroundService.TypeConnectedDevice
            : ForegroundService.TypeMicrophone;

    public override void OnDestroy()
    {
        if (AndroidAudioSession.Current is { } session)
        {
            session.StatusChanged -= OnStatusChanged;
            if (session.Status.State == SessionState.Running) session.StopEngine();
        }
        _instance = null;
        base.OnDestroy();
    }

    /// <summary>Used by the session when a device error forces the session to end.</summary>
    internal static void StopFromSession(SessionStatus status)
    {
        if (_instance is { } service) service.Shutdown(status);
        else AndroidAudioSession.Current?.StopEngine(status);
    }

    void Shutdown(SessionStatus? status)
    {
        var session = AndroidAudioSession.Current;
        if (session is not null)
        {
            session.StatusChanged -= OnStatusChanged;
            session.StopEngine(status);
        }
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    void OnStatusChanged(object? sender, SessionStatus status)
    {
        if (status.State != SessionState.Running) return;
        NotificationManagerCompat.From(this)!.Notify(NotificationId, BuildNotification(status));
    }

    Notification BuildNotification(SessionStatus status)
    {
        var stopIntent = PendingIntent.GetService(this, 1,
            new Intent(this, typeof(VoiceForegroundService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var openIntent = PendingIntent.GetActivity(this, 0,
            PackageManager!.GetLaunchIntentForPackage(PackageName!),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        string effect = Preferences.Default.Get(SettingsKeys.Effect, "Natural");
        string latency = status.OutputLatencyMs is { } ms ? $" · ~{ms:0} ms output buffer" : "";

        return new NotificationCompat.Builder(this, ChannelId)!
            .SetContentTitle($"NeonMouth: {effect}")
            .SetContentText($"{status.Message}{latency}")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true)
            .SetContentIntent(openIntent)
            .AddAction(0, "Stop", stopIntent)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .Build()!;
    }

    void EnsureChannel()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (manager.GetNotificationChannel(ChannelId) is not null) return;
        manager.CreateNotificationChannel(
            new NotificationChannel(ChannelId, "Voice session", NotificationImportance.Low));
    }
}
