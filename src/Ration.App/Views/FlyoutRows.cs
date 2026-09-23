using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Ration.Core.Cost;
using Ration.Core.Model;
using Ration.Core.Usage;

namespace Ration.App.Views;

/// <summary>Panel şablonlarının (FlyoutWindow.xaml) bağlandığı tek seferlik satırlar.</summary>
public sealed record WindowRow(
    string? GroupHeading,
    string Title,
    double Remaining,
    Brush MeterBrush,
    string RemainingText,
    string ResetText,
    string? PaceText,
    string AutomationName)
{
    public Visibility HeadingVisibility => GroupHeading is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PaceVisibility => PaceText is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Ölçer KALAN kotayı gösterir (tepsi ikonuyla aynı dil); renk ise kullanıma göre
    /// uyarı eşiklerini izler (QuotaVisuals.MeterBrush).
    /// </summary>
    public static IReadOnlyList<WindowRow> From(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var stale = snapshot.Status == ProviderStatus.Degraded && snapshot.StaleReason is not null;
        var groups = snapshot.Windows.GroupBy(w => w.GroupName ?? string.Empty, StringComparer.Ordinal).ToList();
        var rows = new List<WindowRow>();

        foreach (var group in groups)
        {
            var heading = groups.Count > 1 && group.Key.Length > 0 ? LocalizeGroup(group.Key) : null;
            foreach (var window in group.OrderBy(w => w.Kind))
            {
                var title = TitleFor(window);
                var remaining = Math.Clamp(100 - window.Percent, 0, 100);
                string? pace = window.Percent >= 100
                    ? PaceCalculator.FormatConsumedBadge(window, now)
                    : !stale && PaceCalculator.Calculate(window, now) is { } p
                        ? PaceCalculator.Format(window, p, now)
                        : null;

                rows.Add(new WindowRow(
                    heading,
                    title,
                    remaining,
                    QuotaVisuals.MeterBrush(window.Percent),
                    // Az kullanım yuvarlamada kaybolmasın: %0,2 kullanımda "%100 kaldı" yerine "%99,8 kaldı".
                    remaining is > 99 and < 99.95 ? $"%{remaining:0.#} kaldı" : $"%{remaining:F0} kaldı",
                    ResetTextFor(window.ResetsAt, stale),
                    pace,
                    $"{title}, yüzde {remaining:F0} kaldı"));
                heading = null;
            }
        }

        return rows;
    }

    private static readonly Regex HourLabel = new(@"^(\d+ saatlik|Saatlik)$", RegexOptions.CultureInvariant);

    private static string TitleFor(UsageWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Label)) return KindName(window.Kind);

        // "5 saatlik" sağlayıcıların oturum penceresidir; CodexBar dilinde "Oturum".
        if (window.Kind == WindowKind.Session && HourLabel.IsMatch(window.Label)) return "Oturum";

        // Codex'in model bazlı ek limitleri iç adla gelir ("gpt-reserve · Haftalık"); kullanıcıya
        // bir şey anlatmaz. Sağ taraf (pencere adı) korunur.
        var parts = window.Label.Split(" · ", 2);
        return parts.Length == 2 && parts[0].StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            ? $"Ek model limiti · {parts[1]}"
            : window.Label;
    }

    /// <summary>Sağlayıcının İngilizce grup adları ("Gemini Models", "Claude and GPT models").</summary>
    private static string LocalizeGroup(string name)
    {
        var text = Regex.Replace(name, @"\s+models$", " modelleri", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(text, @"\s+and\s+", " ve ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string KindName(WindowKind kind) => kind switch
    {
        WindowKind.Session => "Oturum",
        WindowKind.Weekly => "Haftalık",
        WindowKind.Daily => "Günlük",
        WindowKind.Monthly => "Aylık",
        _ => kind.ToString(),
    };

    private static string ResetTextFor(DateTimeOffset? resetsAt, bool stale)
    {
        var text = QuotaVisuals.FormatReset(resetsAt, stale);
        return text switch
        {
            "" => string.Empty,
            "sıfırlandı" or "sıfırlanmış olabilir" or "Sıfırlanma zamanı geldi — doğrulanıyor" => text,
            _ => $"{text} sonra sıfırlanır",
        };
    }
}

public sealed record StatItem(string Label, string Value);

/// <summary>Günlük çubuk. Bugün tam renk, geçmiş günler soluk: göz önce bugüne gitsin.</summary>
public sealed record DayBar(double BarHeight, double BarWidth, string Tip, double BarOpacity);

public static class UsageFormat
{
    /// <summary>319M, 5,9B, 85K — CodexBar'daki kısa token yazımı.</summary>
    public static string Tokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => Short(tokens / 1_000_000_000d) + "B",
        >= 1_000_000 => Short(tokens / 1_000_000d) + "M",
        >= 1_000 => Short(tokens / 1_000d) + "K",
        _ => tokens.ToString(CultureInfo.CurrentCulture),
    };

    private static string Short(double value) =>
        value.ToString(value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture);

    public static string Money(decimal amount, string currency)
    {
        var symbol = currency.ToUpperInvariant() switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "TRY" => "₺",
            _ => currency + " ",
        };
        return amount >= 100 ? $"{symbol}{amount:N0}" : $"{symbol}{amount:N2}";
    }

    /// <summary>
    /// Son <paramref name="days"/> günün çubukları; verisi olmayan gün boş yer tutar ki
    /// eksen düzgün kalsın. Yükseklik en yoğun güne göre ölçeklenir.
    /// </summary>
    public static IReadOnlyList<DayBar> Bars(IReadOnlyList<DailyTokens> daily, DateOnly today, int days, double width, double height)
    {
        const double gap = 3;
        // Aşağı yuvarla: toplam genişlik alanı bir piksel bile aşarsa son çubuk (bugün) kırpılır.
        var barWidth = Math.Max(1, Math.Floor((width - gap * (days - 1)) / days));
        var byDay = daily.ToDictionary(d => d.Day, d => d.Tokens);
        var max = Math.Max(1, daily.Count == 0 ? 1 : daily.Max(d => d.Tokens));

        return Enumerable.Range(0, days)
            .Select(i => today.AddDays(i - days + 1))
            .Select(day =>
            {
                var tokens = byDay.GetValueOrDefault(day);
                var h = tokens <= 0 ? 0 : Math.Max(2, height * tokens / max);
                return new DayBar(h, barWidth, $"{day.ToString("d MMM", CultureInfo.CurrentCulture)} · {Tokens(tokens)} token", day == today ? 1.0 : 0.55);
            })
            .ToList();
    }
}
