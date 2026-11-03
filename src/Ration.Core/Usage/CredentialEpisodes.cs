using Ration.Core.Model;

namespace Ration.Core.Usage;

/// <summary>
/// "A provider needs a new sign-in" notifications. One alert per failure episode (CodexBar's rule): after
/// it is shown the provider stays quiet, and only a successful fresh fetch ends the episode. A provider that
/// never worked has nothing to renew, so it is never reported (a fresh install shows no toast).
/// Pure state; the App layer shows the alert and persists <see cref="EverWorked"/> and <see cref="Notified"/>.
/// </summary>
public sealed class CredentialEpisodes
{
    private readonly HashSet<string> _everWorked;
    private readonly HashSet<string> _notified;

    public CredentialEpisodes(IEnumerable<string> everWorked, IEnumerable<string> notified)
    {
        _everWorked = new HashSet<string>(everWorked, StringComparer.OrdinalIgnoreCase);
        _notified = new HashSet<string>(notified, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Providers that have returned data at least once.</summary>
    public IReadOnlyCollection<string> EverWorked => _everWorked;

    /// <summary>Providers whose current failure episode already produced an alert.</summary>
    public IReadOnlyCollection<string> Notified => _notified;

    /// <summary>Follows the data: a successful fetch records the provider and ends its failure episode. True if the state changed.</summary>
    public bool Track(UsageSnapshot snapshot)
    {
        if (snapshot.Status != ProviderStatus.Ok) return false;

        var changed = _everWorked.Add(snapshot.ProviderId);
        changed |= _notified.Remove(snapshot.ProviderId);
        return changed;
    }

    /// <summary>The alert to show for this snapshot, or null. Showing it must be followed by <see cref="MarkNotified"/>.</summary>
    public QuotaAlert? Pending(UsageSnapshot snapshot, string providerName)
    {
        if (snapshot.Status != ProviderStatus.AuthRequired) return null;
        if (!_everWorked.Contains(snapshot.ProviderId) || _notified.Contains(snapshot.ProviderId)) return null;

        return new QuotaAlert(
            $"{snapshot.ProviderId}|signin",
            L.T($"{providerName} needs a new sign-in", $"{providerName} için yeniden giriş gerekiyor"),
            L.T("Sign in again so Ration can keep the quota up to date.", "Kotanın güncel kalması için yeniden giriş yap."));
    }

    /// <summary>The alert was shown; the provider stays quiet until a fetch succeeds again. True if the state changed.</summary>
    public bool MarkNotified(string providerId) => _notified.Add(providerId);
}
