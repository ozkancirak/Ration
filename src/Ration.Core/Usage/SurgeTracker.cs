using Ration.Core.Model;

namespace Ration.Core.Usage;

/// <summary>
/// "Usage is climbing fast" detection. Refreshes can be minutes apart, so the jump between two samples is
/// turned into a rate: an alert fires when the rate reaches the chosen "N points in 5 minutes". Moves under
/// <see cref="MinimumJump"/> points are noise; samples more than <see cref="MaxGap"/> apart say nothing about
/// "quickly"; after an alert the window stays quiet for <see cref="Cooldown"/>.
/// Only fresh (Ok) data is looked at, and only the main windows, as in <see cref="QuotaAlerts"/>.
/// The samples live in memory: after a restart the first sample is a new baseline.
/// </summary>
public sealed class SurgeTracker
{
    public const int DefaultPointsPerFiveMinutes = 10;

    private const double MinimumJump = 5;
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, (DateTimeOffset At, double Percent)> _samples = new();
    private readonly Dictionary<string, DateTimeOffset> _alertedAt = new();

    public IReadOnlyList<QuotaAlert> Observe(
        UsageSnapshot snapshot,
        string providerName,
        DateTimeOffset now,
        int pointsPerFiveMinutes = DefaultPointsPerFiveMinutes)
    {
        if (snapshot.Status != ProviderStatus.Ok) return Array.Empty<QuotaAlert>();

        var alerts = new List<QuotaAlert>();
        foreach (var window in snapshot.Windows)
        {
            if (window.Kind is not (WindowKind.Session or WindowKind.Weekly)) continue;
            if (window.Label?.Contains("gpt-", StringComparison.OrdinalIgnoreCase) == true) continue;

            var key = $"{snapshot.ProviderId}|{window.GroupName}|{window.Kind}|{window.Label}";
            var percent = Math.Clamp(window.Percent, 0, 100);

            if (!_samples.TryGetValue(key, out var previous))
            {
                _samples[key] = (now, percent);
                continue;
            }

            var span = now - previous.At;
            // Refreshes right after each other (a manual refresh, then the panel opening) keep the older
            // sample, so the jump adds up instead of being cut into pieces too small to notice.
            if (span < MinimumSpan) continue;

            var jump = percent - previous.Percent;
            var minutes = span.TotalMinutes;
            var quiet = _alertedAt.TryGetValue(key, out var last) && now - last < Cooldown;
            if (jump >= MinimumJump && span <= MaxGap && !quiet && jump / minutes * 5 >= pointsPerFiveMinutes)
            {
                _alertedAt[key] = now;
                alerts.Add(CreateAlert(snapshot, providerName, window, jump, minutes, percent));
            }

            _samples[key] = (now, percent);
        }

        return alerts;
    }

    private static QuotaAlert CreateAlert(UsageSnapshot snapshot, string providerName, UsageWindow window, double jump, double minutes, double percent)
    {
        var windowName = window.Kind == WindowKind.Session ? L.T("Session", "Oturum") : window.Label ?? L.T("Weekly", "Haftalık");
        var group = string.IsNullOrWhiteSpace(window.GroupName) ? string.Empty : $" ({window.GroupName})";
        var left = 100 - percent;

        return new QuotaAlert(
            $"{snapshot.ProviderId}|{window.Kind}|surge",
            L.T($"{providerName}{group} · {windowName} usage is climbing fast", $"{providerName}{group} · {windowName} kullanımı hızla artıyor"),
            L.T($"+{jump:F0}% in {minutes:F0} min · {left:F0}% left", $"{minutes:F0} dk'da +%{jump:F0} · %{left:F0} kaldı"));
    }
}
