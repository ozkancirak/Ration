using Ration.Core.Settings;

namespace Ration.Platform.Windows.App;

/// <summary>
/// Genel kısayol tercihi: SettingsStore'da "hotkeyEnabled" (varsayılan kapalı, başka
/// uygulamalarla çakışmasın) ve "hotkey" ("Ctrl+Alt+R") olarak durur.
/// </summary>
public static class HotkeyPreference
{
    private const string EnabledKey = "hotkeyEnabled";
    private const string KeyKey = "hotkey";

    public static event Action? Changed;

    public static bool Enabled => SettingsStore.Default.GetBool(EnabledKey, false);

    public static Hotkey Current =>
        Hotkey.TryParse(SettingsStore.Default.GetString(KeyKey), out var hotkey) ? hotkey : Hotkey.Default;

    public static void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        SettingsStore.Default.Set(EnabledKey, enabled);
        Changed?.Invoke();
    }
}
