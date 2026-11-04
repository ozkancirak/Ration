using Ration.Core.Model;

namespace Ration.Core.Usage;

/// <summary>
/// Banked reset credits expire unused, so one notification is shown when the next expiry comes within
/// <see cref="Lead"/>. The key holds the expiry, so each new deadline gets its own single alert.
/// Like the quota alerts, it ignores stale (Degraded) data.
/// </summary>
public static class ResetCreditAlerts
{
    public static readonly TimeSpan Lead = TimeSpan.FromHours(48);

    public static IReadOnlyList<QuotaAlert> Evaluate(
        UsageSnapshot snapshot,
        string providerName,
        IReadOnlySet<string> alreadyFired,
        DateTimeOffset now)
    {
        if (snapshot.Status != ProviderStatus.Ok ||
            snapshot.ResetCredits is not { Available: > 0, NextExpiry: { } expiry } credits ||
            expiry <= now ||
            expiry - now > Lead)
        {
            return Array.Empty<QuotaAlert>();
        }

        var key = $"{snapshot.ProviderId}|resetcredit|{expiry.ToUnixTimeSeconds()}";
        if (alreadyFired.Contains(key)) return Array.Empty<QuotaAlert>();

        return
        [
            new QuotaAlert(
                key,
                L.T($"{providerName} · reset credit expires soon", $"{providerName} · sıfırlama kredisi bitmek üzere"),
                Describe(credits, now)),
        ];
    }

    /// <summary>"2 reset credits banked · next expires in 5d 3h".</summary>
    public static string Describe(ResetCredits credits, DateTimeOffset now)
    {
        var count = credits.Available;
        var banked = L.T(
            $"{count} reset {(count == 1 ? "credit" : "credits")} banked",
            $"{count} birikmiş sıfırlama kredisi");

        if (credits.NextExpiry is not { } expiry) return banked;

        return QuotaAlerts.FormatUntil(expiry, now) is { } left
            ? banked + L.T($" · next expires in {left}", $" · en yakını {left} sonra bitiyor")
            : banked + L.T(" · next one has expired", " · en yakını doldu");
    }
}
