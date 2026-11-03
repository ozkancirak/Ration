using System.Globalization;
using Ration.Core.Model;
using Ration.Platform.Windows.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ration.App.Views;

/// <summary>
/// The single source of quota visualization.
///
/// Colors are NOT HARD-CODED; they are read from WinUI theme brushes:
/// - normal       → AccentFillColorDefaultBrush  (the user's Windows accent color)
/// - 75% and above → SystemFillColorCautionBrush
/// - 90% and above → SystemFillColorCriticalBrush
/// - High contrast → SystemColorHighlightColorBrush / SystemColorWindowTextColorBrush
///
/// So light/dark theme, high contrast and accent color changes come for free.
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

    /// <summary>"2h 14m", "38m", "3d" — writes the time to reset compactly.</summary>
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
    /// Applies a window to the meter, the percent text, the reset text and the accessibility name.
    /// If there is no window the meter is left empty and faded — a fake zero is not shown.
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

    /// <summary>"Updated just now", "Updated 12 min ago" — data freshness in one line.</summary>
    public static string FormatUpdated(DateTimeOffset fetchedAt)
    {
        var age = FormatAge(fetchedAt);
        return L.T($"Updated {age}", char.ToUpper(age[0], CultureInfo.CurrentCulture) + age[1..] + " güncellendi");
    }

    /// <summary>"just now", "12 min ago", "3 h ago" — relative age, in the UI language.</summary>
    public static string FormatAge(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        if (age < TimeSpan.FromMinutes(1)) return L.T("just now", "az önce");
        if (age.TotalHours < 1) return L.T($"{(int)age.TotalMinutes} min ago", $"{(int)age.TotalMinutes} dk önce");
        if (age.TotalDays < 1) return L.T($"{(int)age.TotalHours} h ago", $"{(int)age.TotalHours} sa önce");
        return L.T($"{(int)age.TotalDays} d ago", $"{(int)age.TotalDays} gün önce");
    }

    /// <summary>Theme brush; gray if missing from the dictionary. No fixed color is used in code.</summary>
    internal static Brush Fill(string key) => Resource(key);

    /// <summary>Applies the type ramp style; leaves the default if it is not in the dictionary.</summary>
    internal static void SetTextStyle(TextBlock text, string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Style style)
        {
            text.Style = style;
        }
    }

    /// <summary>Control corner radius (for pills/badges and tabs).</summary>
    internal static CornerRadius PillCorner()
    {
        if (Application.Current.Resources.TryGetValue("ControlCornerRadius", out var value) && value is CornerRadius corner)
        {
            return corner;
        }
        return new CornerRadius(4);
    }

    /// <summary>
    /// Shows or hides the plan badge. Antigravity returns "Google AI Pro/Ultra";
    /// we strip the product family prefix from the badge and show only the plan.
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

        // Do not crash the app if the theme dictionary is unexpectedly missing something.
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
