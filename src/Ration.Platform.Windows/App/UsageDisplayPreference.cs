using Ration.Core.Settings;

namespace Ration.Platform.Windows.App;

/// <summary>
/// Whether the panel and the tray tooltip show the used percent instead of the remaining one.
/// Stored in SettingsStore as "showUsed"; off by default (the meters show what is left).
/// </summary>
public static class UsageDisplayPreference
{
    private const string SettingKey = "showUsed";

    /// <summary>The choice changed; windows showing percentages should redraw.</summary>
    public static event Action? Changed;

    public static bool ShowUsed => SettingsStore.Default.GetBool(SettingKey, false);

    public static void Set(bool showUsed)
    {
        if (ShowUsed == showUsed) return;
        SettingsStore.Default.Set(SettingKey, showUsed);
        Changed?.Invoke();
    }
}
