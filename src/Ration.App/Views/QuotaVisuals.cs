using System.Globalization;
using Ration.Core.Model;
using Ration.Platform.Windows.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ration.App.Views;

/// <summary>
/// Kota görselleştirmesinin tek kaynağı.
///
/// Renkler KOD İÇİNDE SABİTLENMEZ; WinUI tema fırçalarından okunur:
/// - normal       → AccentFillColorDefaultBrush  (kullanıcının Windows accent rengi)
/// - %75 ve üzeri → SystemFillColorCautionBrush
/// - %90 ve üzeri → SystemFillColorCriticalBrush
/// - Yüksek kontrast → SystemColorHighlightColorBrush / SystemColorWindowTextColorBrush
///
/// Böylece açık/koyu tema, yüksek kontrast ve accent rengi değişimi bedava gelir.
/// </summary>
public static class QuotaVisuals
{
    private const double CautionThreshold = 75;
    private const double CriticalThreshold = 90;

    public static Brush MeterBrush(double percent)
    {
        if (SystemAccent.IsHighContrast)
        {
            return Resource("SystemColorHighlightColorBrush");
        }

        var key = percent switch
        {
            >= CriticalThreshold => "SystemFillColorCriticalBrush",
            >= CautionThreshold => "SystemFillColorCautionBrush",
            _ => "AccentFillColorDefaultBrush",
        };

        return Resource(key);
    }

    /// <summary>"2 sa 14 dk", "38 dk", "3 gün" — sıfırlanmaya süreyi kısa yazar.</summary>
    public static string FormatReset(DateTimeOffset? resetsAt, bool stale = false)
    {
        if (resetsAt is not { } reset) return string.Empty;

        var remaining = reset - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            return stale
                ? L.T("may have reset", "sıfırlanmış olabilir")
                : L.T("Reset time reached — verifying", "Sıfırlanma zamanı geldi — doğrulanıyor");
        }
        if (remaining.TotalMinutes < 60) return L.T($"{(int)remaining.TotalMinutes}m", $"{(int)remaining.TotalMinutes} dk");
        if (remaining.TotalHours < 24) return L.T($"{(int)remaining.TotalHours}h {remaining.Minutes}m", $"{(int)remaining.TotalHours} sa {remaining.Minutes} dk");

        return L.T($"{(int)remaining.TotalDays}d", $"{(int)remaining.TotalDays} gün");
    }

    /// <summary>
    /// Bir pencereyi ölçere, yüzde yazısına, sıfırlanma yazısına ve erişilebilirlik ismine uygular.
    /// Pencere yoksa ölçer boş ve soluk bırakılır — sahte sıfır gösterilmez.
    /// </summary>
    public static void Apply(
        UsageWindow? window,
        ProgressBar bar,
        TextBlock percentText,
        TextBlock? resetText = null,
        TextBlock? labelText = null,
        string? automationPrefix = null)
    {
        if (window is null)
        {
            bar.Value = 0;
            bar.Foreground = Resource("ControlStrongFillColorDisabledBrush");
            percentText.Text = "—";

            if (resetText is not null) resetText.Text = string.Empty;
            if (!string.IsNullOrWhiteSpace(automationPrefix))
            {
                AutomationProperties.SetName(bar, L.T($"{automationPrefix}: no data", $"{automationPrefix}: veri yok"));
            }
            return;
        }

        bar.Value = window.Percent;
        bar.Foreground = MeterBrush(window.Percent);
        percentText.Text = L.T($"{window.Percent:F0}%", $"%{window.Percent:F0}");

        if (labelText is not null && !string.IsNullOrWhiteSpace(window.Label))
        {
            labelText.Text = window.Label;
        }

        if (resetText is not null)
        {
            resetText.Text = FormatReset(window.ResetsAt);
        }

        if (!string.IsNullOrWhiteSpace(automationPrefix))
        {
            AutomationProperties.SetName(bar, L.T($"{automationPrefix}, {window.Percent:F0} percent", $"{automationPrefix}, yüzde {window.Percent:F0}"));
        }
    }

    /// <summary>"Az önce güncellendi", "12 dk önce güncellendi" — veri tazeliği tek satır.</summary>
    public static string FormatUpdated(DateTimeOffset fetchedAt)
    {
        var age = FormatAge(fetchedAt);
        return L.T($"Updated {age}", char.ToUpper(age[0], CultureInfo.CurrentCulture) + age[1..] + " güncellendi");
    }

    /// <summary>"az önce", "12 dk önce" / "just now", "12 min ago".</summary>
    public static string FormatAge(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        if (age < TimeSpan.FromMinutes(1)) return L.T("just now", "az önce");
        if (age.TotalHours < 1) return L.T($"{(int)age.TotalMinutes} min ago", $"{(int)age.TotalMinutes} dk önce");
        if (age.TotalDays < 1) return L.T($"{(int)age.TotalHours} h ago", $"{(int)age.TotalHours} sa önce");
        return L.T($"{(int)age.TotalDays} d ago", $"{(int)age.TotalDays} gün önce");
    }

    /// <summary>Tema fırçası; sözlükte yoksa gri. Kodda sabit renk kullanılmaz.</summary>
    internal static Brush Fill(string key) => Resource(key);

    /// <summary>Tip rampası stilini uygular; sözlükte yoksa varsayılanı bırakır.</summary>
    internal static void SetTextStyle(TextBlock text, string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Style style)
        {
            text.Style = style;
        }
    }

    /// <summary>Kontrol köşe yarıçapı (hap/rozet ve sekmeler için).</summary>
    internal static CornerRadius PillCorner()
    {
        if (Application.Current.Resources.TryGetValue("ControlCornerRadius", out var value) && value is CornerRadius corner)
        {
            return corner;
        }
        return new CornerRadius(4);
    }

    /// <summary>
    /// Plan rozetini gösterir ya da gizler. Antigravity "Google AI Pro/Ultra"
    /// döndürür; ürün ailesi önekini rozetten çıkarıp yalnızca planı gösteririz.
    /// </summary>
    public static void ApplyPlan(Border badge, TextBlock text, string? planName)
    {
        if (string.IsNullOrWhiteSpace(planName))
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }

        var displayName = planName.Trim();
        const string googleAiPrefix = "Google AI ";
        if (displayName.StartsWith(googleAiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            displayName = displayName[googleAiPrefix.Length..].Trim();
        }

        if (displayName.Length == 0)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }

        text.Text = char.ToUpper(displayName[0], System.Globalization.CultureInfo.CurrentCulture) + displayName[1..];
        badge.Visibility = Visibility.Visible;
    }

    private static Brush Resource(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        // Tema sözlüğü beklenmedik şekilde eksikse uygulamayı çökertme.
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
