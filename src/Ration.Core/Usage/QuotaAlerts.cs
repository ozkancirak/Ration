using Ration.Core.Model;

namespace Ration.Core.Usage;

public sealed record QuotaAlert(string Key, string Title, string Body);

/// <summary>
/// Kota bildirimi kararı (saf; gösterim App katmanında). Ana pencerelerde (oturum/haftalık)
/// kalan kota %20'nin altına inince "azaldı", bitince "doldu" bildirilir. Her eşik her
/// pencere döngüsünde bir kez: anahtar sıfırlanma zamanını içerir, yeni döngü yeni anahtar.
/// Bayat (Degraded) veride bildirim üretilmez; eski veriye göre alarm çalmak yanlıştır.
/// </summary>
public static class QuotaAlerts
{
    public const double LowRemaining = 20;

    public static IReadOnlyList<QuotaAlert> Evaluate(
        UsageSnapshot snapshot,
        string providerName,
        IReadOnlySet<string> alreadyFired,
        DateTimeOffset now)
    {
        if (snapshot.Status != ProviderStatus.Ok) return Array.Empty<QuotaAlert>();

        var alerts = new List<QuotaAlert>();
        foreach (var window in snapshot.Windows)
        {
            if (window.Kind is not (WindowKind.Session or WindowKind.Weekly)) continue;
            // Model bazlı ek limitler (gpt-reserve vb.) tepsi hesabında olduğu gibi dışarıda.
            if (window.Label?.Contains("gpt-", StringComparison.OrdinalIgnoreCase) == true) continue;

            var remaining = Math.Clamp(100 - window.Percent, 0, 100);
            var (level, threshold) = remaining <= 0 ? ("doldu", "0") : remaining <= LowRemaining ? ("azaldı", "20") : (null, null);
            if (level is null) continue;

            // Claude'da Haftalık, Haftalık · Opus, Haftalık · Sonnet aynı türdedir; etiket ayırır.
            var windowName = window.Kind == WindowKind.Session ? "Oturum" : window.Label ?? "Haftalık";
            // Sıfırlanma zamanı bazı kaynaklarda "N sn sonra"dan hesaplanır ve sorgudan sorguya
            // birkaç saniye kayar; 10 dk dilime yuvarlanmazsa aynı bildirim tekrar tekrar gelir.
            var cycle = window.ResetsAt is { } r ? ((r.ToUnixTimeSeconds() + 300) / 600).ToString() : "-";
            var key = $"{snapshot.ProviderId}|{window.GroupName}|{window.Kind}|{window.Label}|{cycle}|{threshold}";
            if (alreadyFired.Contains(key)) continue;

            var group = string.IsNullOrWhiteSpace(window.GroupName) ? string.Empty : $" ({window.GroupName})";
            var reset = FormatUntil(window.ResetsAt, now);
            var body = remaining <= 0
                ? (reset is null ? "Kota tükendi." : $"{reset} sonra sıfırlanır.")
                : $"%{remaining:F0} kaldı" + (reset is null ? "." : $" · {reset} sonra sıfırlanır.");

            alerts.Add(new QuotaAlert(key, $"{providerName}{group} · {windowName} kotası {level}", body));
        }

        return alerts;
    }

    private static string? FormatUntil(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset || reset <= now) return null;
        var left = reset - now;
        if (left.TotalMinutes < 60) return $"{Math.Max(1, (int)left.TotalMinutes)} dk";
        if (left.TotalHours < 24) return $"{(int)left.TotalHours} sa {left.Minutes} dk";
        return $"{(int)left.TotalDays} gün {left.Hours} sa";
    }
}
