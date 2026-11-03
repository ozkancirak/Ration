using Ration.Core.Settings;

namespace Ration.Platform.Windows.App;

/// <summary>
/// Global hotkey preference: stored in SettingsStore as "hotkeyEnabled" (off by default, so it does not
/// conflict with other apps) and "hotkey" ("Ctrl+Alt+R").
/// </summary>
public static class HotkeyPreference
{
    private const string EnabledKey = "hotkeyEnabled";
    private const string KeyKey = "hotkey";

    public static event Action? Changed;

    /// <summary>The registration result changed (<see cref="RegistrationFailed"/>).</summary>
    public static event Action? RegistrationChanged;

    public static bool Enabled => SettingsStore.Default.GetBool(EnabledKey, false);

    public static Hotkey Current =>
        Hotkey.TryParse(SettingsStore.Default.GetString(KeyKey), out var hotkey) ? hotkey : Hotkey.Default;

    /// <summary>True while a new combination is being captured in settings: the registered hotkey is released
    /// temporarily so that it does not steal the pressed keys from the window.</summary>
    public static bool Capturing { get; private set; }

    public static void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        SettingsStore.Default.Set(EnabledKey, enabled);
        Changed?.Invoke();
    }

    public static void Set(Hotkey hotkey)
    {
        if (Current == hotkey) return;
        SettingsStore.Default.Set(KeyKey, hotkey.ToString());
        Changed?.Invoke();
    }

    /// <summary>The last registration attempt failed: another app holds the combination.</summary>
    public static bool RegistrationFailed { get; private set; }

    public static void ReportRegistration(bool failed)
    {
        if (RegistrationFailed == failed) return;
        RegistrationFailed = failed;
        RegistrationChanged?.Invoke();
    }

    public static void SetCapturing(bool capturing)
    {
        if (Capturing == capturing) return;
        Capturing = capturing;
        Changed?.Invoke();
    }
}
