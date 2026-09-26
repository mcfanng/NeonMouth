namespace NeonMouth;

/// <summary>Keys for values persisted with MAUI <see cref="Preferences"/>.</summary>
public static class SettingsKeys
{
    public const string Effect = "effect";
    public const string Intensity = "intensity";
    public const string OutputGain = "output_gain";
    public const string LastBluetoothOutput = "last_bt_output";
    /// <summary>"|"-separated keys of Bluetooth speakers the user has used or reactivated before.</summary>
    public const string KnownBluetoothOutputs = "known_bt_outputs";
    public const string DisclosureAccepted = "mic_disclosure_accepted";
    /// <summary>Howling protection on/off (§3.2, §4.1); default on.</summary>
    public const string FeedbackProtection = "feedback_protection";
}
