using Ration.Core.Abstractions;
using Ration.Core.Model;

namespace Ration.Core.Providers;

public static class ProviderResolver
{
    /// <summary>
    /// Tries the source chain in priority order; the first one returning <see cref="ProviderStatus.Ok"/>
    /// wins. If none succeeds, the first meaningful error is returned.
    /// By contract it never returns null and never leaks an exception.
    /// </summary>
    public static async Task<UsageSnapshot> ResolveAsync(
        IUsageProvider provider,
        CancellationToken ct = default,
        Action<UsageSnapshot>? onProgress = null)
    {
        UsageSnapshot? firstProblem = null;
        UsageSnapshot? staleWithData = null;

        foreach (var source in provider.Sources)
        {
            bool available;
            try
            {
                available = await source.IsAvailableAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                available = false;
            }

            if (!available) continue;

            UsageSnapshot snapshot;
            var progressive = source as IProgressiveUsageSource;
            if (progressive is not null && onProgress is not null)
            {
                progressive.SnapshotUpdated += onProgress;
            }

            try
            {
                snapshot = await source.FetchAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Sources must not leak exceptions; this is only a safety net.
                snapshot = Snapshot.Empty(
                    provider.Id,
                    ProviderStatus.Error,
                    L.T($"{source.Kind} unexpected error: {ex.GetType().Name}", $"{source.Kind} beklenmedik hata: {ex.GetType().Name}"),
                    source.Kind);
            }
            finally
            {
                if (progressive is not null && onProgress is not null)
                {
                    progressive.SnapshotUpdated -= onProgress;
                }
            }

            if (snapshot.Status == ProviderStatus.Ok) return snapshot;

            // A stale result that contains data (e.g. a statusLine record) is better than an error that contains none.
            if (snapshot.Status == ProviderStatus.Degraded && snapshot.Windows.Count > 0)
            {
                staleWithData ??= snapshot;
            }
            else
            {
                firstProblem ??= snapshot;
            }
        }

        return staleWithData ?? firstProblem ?? Snapshot.Empty(
            provider.Id,
            ProviderStatus.AuthRequired,
            L.T("No usable source. Make sure you are signed in with the provider's CLI.", "Kullanılabilir kaynak yok. İlgili CLI ile giriş yapıldığından emin olun."));
    }
}
