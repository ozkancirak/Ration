namespace Kalan.Core.Model;

/// <summary>
/// UsageSnapshot üretmek için kısayollar.
/// AGENTS.md §4: FetchAsync asla null dönmez ve asla exception sızdırmaz —
/// hata durumunda da dolu bir snapshot döner.
/// </summary>
public static class Snapshot
{
    public static UsageSnapshot Empty(
        string providerId,
        ProviderStatus status,
        string? reason,
        SourceKind? resolvedVia = null) =>
        new(
            ProviderId: providerId,
            Windows: Array.Empty<UsageWindow>(),
            Credits: null,
            Cost: null,
            Status: status,
            ResolvedVia: resolvedVia,
            FetchedAt: DateTimeOffset.UtcNow,
            StaleReason: reason);
}
