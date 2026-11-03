namespace Ration.Core.Model;

/// <summary>
/// Shortcuts for producing a UsageSnapshot.
/// FetchAsync never returns null and never leaks an exception —
/// even on error it returns a populated snapshot.
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
