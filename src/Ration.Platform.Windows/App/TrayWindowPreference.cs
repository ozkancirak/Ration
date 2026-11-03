using Ration.Core.Settings;
using Ration.Core.Usage;

namespace Ration.Platform.Windows.App;

/// <summary>
/// Which window the tray icon follows: the most used one (default), the session or the weekly window.
/// Stored in SettingsStore as "trayWindow" ("auto", "session", "weekly").
/// </summary>
public static class TrayWindowPreference
{
    private const string SettingKey = "trayWindow";

    /// <summary>The choice changed; the tray icon should be recalculated.</summary>
    public static event Action? Changed;

    public static TrayWindowChoice Current => FromTag(SettingsStore.Default.GetString(SettingKey));

    public static void Set(TrayWindowChoice choice)
    {
        if (Current == choice) return;
        SettingsStore.Default.Set(SettingKey, ToTag(choice));
        Changed?.Invoke();
    }

    public static string ToTag(TrayWindowChoice choice) => choice switch
    {
        TrayWindowChoice.Session => "session",
        TrayWindowChoice.Weekly => "weekly",
        _ => "auto",
    };

    public static TrayWindowChoice FromTag(string? tag) => tag switch
    {
        "session" => TrayWindowChoice.Session,
        "weekly" => TrayWindowChoice.Weekly,
        _ => TrayWindowChoice.Highest,
    };
}
