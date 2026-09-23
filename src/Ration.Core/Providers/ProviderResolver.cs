using Ration.Core.Abstractions;
using Ration.Core.Model;

namespace Ration.Core.Providers;

public static class ProviderResolver
{
    /// <summary>
    /// Kaynak zincirini öncelik sırasıyla dener; ilk <see cref="ProviderStatus.Ok"/>
    /// dönen kazanır. Hiçbiri başarılı olmazsa ilk anlamlı hata döner.
    /// Sözleşme gereği asla null dönmez ve asla exception sızdırmaz.
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
                // Kaynaklar exception sızdırmamalı; bu yalnızca güvenlik ağı.
                snapshot = Snapshot.Empty(
                    provider.Id,
                    ProviderStatus.Error,
                    $"{source.Kind} beklenmedik hata: {ex.GetType().Name}",
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

            // Veri içeren bayat sonuç (örn. statusLine kaydı), veri içermeyen hatadan iyidir.
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
