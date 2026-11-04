using System.Net.Http;
using Ration.App.Updates;
using Ration.Core.Diagnostics;
using Ration.Core.Providers.Status;
using Ration.Platform.Windows.App;

namespace Ration.App.Views;

/// <summary>
/// What the providers' public status pages say right now. Checked when the panel opens and when a refresh
/// failed (that is when an outage explains something), at most every five minutes. A check that fails keeps
/// what was known: a status page that is down says nothing about the provider.
/// </summary>
internal static class ProviderOutages
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, StatusReport> Reports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static readonly HttpClient Http = CreateClient();
    private static DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private static int _running;

    /// <summary>The reports changed; raised on a background thread.</summary>
    public static event Action? Changed;

    public static StatusReport? For(string providerId)
    {
        lock (Gate) return Reports.GetValueOrDefault(providerId);
    }

    /// <summary>Forgets every report (the user turned the checks off).</summary>
    public static void Clear()
    {
        bool had;
        lock (Gate)
        {
            had = Reports.Count > 0;
            Reports.Clear();
            _checkedAt = DateTimeOffset.MinValue;
        }

        if (had) Changed?.Invoke();
    }

    public static async Task RefreshAsync(IEnumerable<string> providerIds)
    {
        if (!ProviderStatusPreference.Enabled) return;
        if (DateTimeOffset.UtcNow - _checkedAt < CacheTime) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        try
        {
            _checkedAt = DateTimeOffset.UtcNow;
            var changed = false;

            foreach (var id in providerIds)
            {
                if (ProviderStatusPages.For(id) is not { } source) continue;

                var report = await StatusPageClient.FetchAsync(Http, source).ConfigureAwait(false);
                if (report is null) continue;

                lock (Gate)
                {
                    if (Reports.TryGetValue(id, out var known) && known == report) continue;
                    Reports[id] = report;
                }

                changed = true;
            }

            if (changed) Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Trace.Error("status", $"refresh failed type={ex.GetType().Name}");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static HttpClient CreateClient()
    {
        // No redirects: the feed addresses are exact, and a redirect would send the request somewhere we did not choose.
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Ration/{AppUpdates.CurrentVersion}");
        return client;
    }
}
