using Ration.Core.Model;

namespace Ration.Core.Usage;

/// <param name="Forget">Fired keys that are obsolete once this alert is shown (a reset closes the "used up" alerts before it).</param>
public sealed record QuotaAlert(string Key, string Title, string Body, IReadOnlyList<string>? Forget = null);

/// <summary>
/// Quota notification decision (pure; display is in the App layer). In the main windows (session/weekly),
/// "running low" is notified when the remaining quota falls below the threshold (default 20%), and "used up"
/// when it runs out. Each threshold fires once per window cycle: the key includes the reset time, a new cycle
/// means a new key. No notification is produced for stale (Degraded) data; raising an alarm based on old
/// data is wrong.
/// </summary>
public static class QuotaAlerts
{
    public const double LowRemaining = 20;

    public static IReadOnlyList<QuotaAlert> Evaluate(
        UsageSnapshot snapshot,
        string providerName,
        IReadOnlySet<string> alreadyFired,
        DateTimeOffset now,
        double lowRemaining = LowRemaining,
        bool includeResets = false)
    {
        if (snapshot.Status != ProviderStatus.Ok) return Array.Empty<QuotaAlert>();

        var alerts = new List<QuotaAlert>();
        foreach (var window in snapshot.Windows)
        {
            if (window.Kind is not (WindowKind.Session or WindowKind.Weekly)) continue;
            // Per-model additional limits (gpt-reserve etc.) are left out, as in the tray calculation.
            if (window.Label?.Contains("gpt-", StringComparison.OrdinalIgnoreCase) == true) continue;

            var remaining = Math.Clamp(100 - window.Percent, 0, 100);

            // In Claude, Weekly, Weekly · Opus and Weekly · Sonnet are the same kind; the label tells them apart.
            var windowName = window.Kind == WindowKind.Session ? L.T("Session", "Oturum") : window.Label ?? L.T("Weekly", "Haftalık");
            // In some sources the reset time is computed from "in N seconds" and drifts by a few seconds
            // from query to query; if it is not rounded to a 10-minute slice, the same notification comes again and again.
            var cycle = window.ResetsAt is { } r ? ((r.ToUnixTimeSeconds() + 300) / 600).ToString() : "-";
            var windowKey = $"{snapshot.ProviderId}|{window.GroupName}|{window.Kind}|{window.Label}|";
            var group = string.IsNullOrWhiteSpace(window.GroupName) ? string.Empty : $" ({window.GroupName})";

            // A window that was used up in an earlier cycle has quota again: the reset happened. The "used up"
            // keys of the older cycles are forgotten so the next reset needs a new "used up" first.
            if (includeResets && remaining > 0 && cycle != "-")
            {
                var resetKey = $"{windowKey}{cycle}|reset";
                var currentUsedUp = $"{windowKey}{cycle}|0";
                var usedUpBefore = alreadyFired
                    .Where(k => k.StartsWith(windowKey, StringComparison.Ordinal) && k.EndsWith("|0", StringComparison.Ordinal) && k != currentUsedUp)
                    .ToList();
                if (usedUpBefore.Count > 0 && !alreadyFired.Contains(resetKey))
                {
                    alerts.Add(new QuotaAlert(
                        resetKey,
                        L.T($"{providerName}{group} · {windowName} quota reset", $"{providerName}{group} · {windowName} kotası sıfırlandı"),
                        L.T($"Fresh quota: {remaining:F0}% left.", $"Yeni kota: %{remaining:F0} kaldı."),
                        usedUpBefore));
                }
            }

            var (level, threshold) = remaining <= 0 ? (L.T("used up", "doldu"), "0") : remaining <= lowRemaining ? (L.T("running low", "azaldı"), lowRemaining.ToString("0", System.Globalization.CultureInfo.InvariantCulture)) : (null, null);
            if (level is null) continue;

            var key = $"{windowKey}{cycle}|{threshold}";
            if (alreadyFired.Contains(key)) continue;

            var reset = FormatUntil(window.ResetsAt, now);
            var body = remaining <= 0
                ? (reset is null ? L.T("Quota used up.", "Kota tükendi.") : L.T($"Resets in {reset}.", $"{reset} sonra sıfırlanır."))
                : L.T($"{remaining:F0}% left", $"%{remaining:F0} kaldı") + (reset is null ? "." : L.T($" · resets in {reset}.", $" · {reset} sonra sıfırlanır."));

            alerts.Add(new QuotaAlert(key, L.T($"{providerName}{group} · {windowName} quota {level}", $"{providerName}{group} · {windowName} kotası {level}"), body));
        }

        return alerts;
    }

    private static string? FormatUntil(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset || reset <= now) return null;
        var left = reset - now;
        if (left.TotalMinutes < 60) return L.T($"{Math.Max(1, (int)left.TotalMinutes)}m", $"{Math.Max(1, (int)left.TotalMinutes)} dk");
        if (left.TotalHours < 24) return L.T($"{(int)left.TotalHours}h {left.Minutes}m", $"{(int)left.TotalHours} sa {left.Minutes} dk");
        return L.T($"{(int)left.TotalDays}d {left.Hours}h", $"{(int)left.TotalDays} gün {left.Hours} sa");
    }
}
