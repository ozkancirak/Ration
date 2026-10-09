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
/// İki dilli metin. Çevirisi çağrı yerinde yan yana durur: <c>L.T("Refresh", "Yenile")</c>.
/// Dil açılışta bir kez belirlenir; değişince uygulama kendini yeniden başlatır (tema gibi).
/// </summary>
public static class L
{
    public static bool Turkish { get; set; } = Resolve(Preference);

    public static string T(string en, string tr) => Turkish ? tr : en;

    /// <summary>Kayıtlı seçim (settings.json, "language"); yoksa Sistem.</summary>
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
    /// Tarih, sayı ve ay adları arayüz diliyle uyuşsun. Bölge biçimi zaten aynı dildeyse
    /// (en-GB, tr-TR) kullanıcınınki korunur; değilse dilin varsayılanına geçilir.
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
