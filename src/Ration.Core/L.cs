using System.Globalization;
using Ration.Core.Settings;

namespace Ration.Core;

public enum AppLanguage
{
    System,
    English,
    Turkish,
}

/// <summary>
/// Bilingual text. The translation sits next to it at the call site: <c>L.T("Refresh", "Yenile")</c>.
/// The language is decided once at startup; when it changes, the app restarts itself (like the theme).
/// </summary>
public static class L
{
    public static bool Turkish { get; set; } = Resolve(Preference);

    public static string T(string en, string tr) => Turkish ? tr : en;

    /// <summary>The saved choice (settings.json, "language"); if none, System.</summary>
    public static AppLanguage Preference
    {
        get => FromTag(SettingsStore.Default.GetString(SettingKey));
        set => SettingsStore.Default.Set(SettingKey, ToTag(value));
    }

    private const string SettingKey = "language";

    public static bool Resolve(AppLanguage language) => language switch
    {
        AppLanguage.Turkish => true,
        AppLanguage.English => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr",
    };

    /// <summary>
    /// Makes dates, numbers and month names match the UI language. If the regional format is already in the same
    /// language (en-GB, tr-TR) the user's own is kept; otherwise it switches to the language's default.
    /// </summary>
    public static void ApplyCulture()
    {
        var language = Turkish ? "tr" : "en";
        var culture = CultureInfo.CurrentCulture.TwoLetterISOLanguageName == language
            ? CultureInfo.CurrentCulture
            : CultureInfo.GetCultureInfo(Turkish ? "tr-TR" : "en-US");
        var uiCulture = CultureInfo.GetCultureInfo(Turkish ? "tr-TR" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = uiCulture;
    }

    public static string ToTag(AppLanguage language) => language switch
    {
        AppLanguage.English => "en",
        AppLanguage.Turkish => "tr",
        _ => "system",
    };

    public static AppLanguage FromTag(string? tag) => tag?.ToLowerInvariant() switch
    {
        "en" => AppLanguage.English,
        "tr" => AppLanguage.Turkish,
        _ => AppLanguage.System,
    };

}
