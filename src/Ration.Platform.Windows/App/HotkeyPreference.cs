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

    /// <summary>Kayıt sonucu değişti (<see cref="RegistrationFailed"/>).</summary>
    public static event Action? RegistrationChanged;

    public static bool Enabled => SettingsStore.Default.GetBool(EnabledKey, false);

    public static Hotkey Current =>
        Hotkey.TryParse(SettingsStore.Default.GetString(KeyKey), out var hotkey) ? hotkey : Hotkey.Default;

    /// <summary>Ayarlarda yeni birleşim yakalanırken true: kayıtlı kısayol, basılan tuşları
    /// pencereden çalmasın diye geçici olarak bırakılır.</summary>
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

    /// <summary>Son kayıt denemesi başarısız oldu: birleşimi başka bir uygulama tutuyor.</summary>
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
