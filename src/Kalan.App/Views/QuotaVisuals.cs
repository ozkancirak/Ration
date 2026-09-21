using Kalan.Core.Model;
using Kalan.Platform.Windows.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Kalan.App.Views;

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

    /// <summary>"2 sa 14 dk", "38 dk", "3 gün" — kalan süreyi kısa yazar.</summary>
    public static string FormatReset(DateTimeOffset? resetsAt, bool stale = false)
    {
        if (resetsAt is not { } reset) return string.Empty;

        var remaining = reset - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            return stale ? "sıfırlanmış olabilir" : "sıfırlandı";
        }
        if (remaining.TotalMinutes < 60) return $"{(int)remaining.TotalMinutes} dk";
        if (remaining.TotalHours < 24) return $"{(int)remaining.TotalHours} sa {remaining.Minutes} dk";

        return $"{(int)remaining.TotalDays} gün";
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
                AutomationProperties.SetName(bar, $"{automationPrefix}: veri yok");
            }
            return;
        }

        bar.Value = window.Percent;
        bar.Foreground = MeterBrush(window.Percent);
        percentText.Text = $"%{window.Percent:F0}";

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
            AutomationProperties.SetName(bar, $"{automationPrefix}, yüzde {window.Percent:F0}");
        }
    }

    /// <summary>"Az önce güncellendi", "12 dk önce güncellendi" — veri tazeliği tek satır.</summary>
    public static string FormatUpdated(DateTimeOffset fetchedAt)
    {
        var age = DateTimeOffset.UtcNow - fetchedAt;

        if (age < TimeSpan.FromMinutes(1)) return "Az önce güncellendi";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} dk önce güncellendi";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} sa önce güncellendi";

        return $"{(int)age.TotalDays} gün önce güncellendi";
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

        text.Text = displayName.ToUpperInvariant();
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
