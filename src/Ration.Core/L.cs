using System.Globalization;
using System.Text.Json;
using Ration.Core.Diagnostics;
using Ration.Core.Providers;

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

    /// <summary>Kayıtlı seçim (language.json); yoksa Sistem.</summary>
    public static AppLanguage Preference
    {
        get
        {
            try
            {
                if (!File.Exists(PreferencePath)) return AppLanguage.System;
                using var document = JsonDocument.Parse(File.ReadAllText(PreferencePath));
                return document.RootElement.TryGetProperty("language", out var value)
                    ? FromTag(value.GetString())
                    : AppLanguage.System;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                Trace.Error("language", $"preference-read failed type={ex.GetType().Name}");
                return AppLanguage.System;
            }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(KnownPaths.CacheDir);
                File.WriteAllText(PreferencePath, $"{{\"language\":\"{ToTag(value)}\"}}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.Error("language", $"preference-write failed type={ex.GetType().Name}");
            }
        }
    }

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

    private static string PreferencePath => Path.Combine(KnownPaths.CacheDir, "language.json");
}
