using Ration.Core.Model;

namespace Ration.Core.Usage;

/// <summary>Result of the pace calculation: will the quota run out before the window ends?</summary>
/// <param name="Tempo">used ratio − elapsed ratio (−1..+1). Positive = ahead of pace.</param>
/// <param name="DepletesIn">Estimated time to reach 100% at the current pace.</param>
public sealed record PaceResult(double Tempo, TimeSpan DepletesIn);

/// <summary>
/// The pure calculation behind the question "will my quota run out before the window ends?".
/// No calculation is done in the UI; it calls here and gets the text via <see cref="Format"/>.
/// In cases that should not be shown, <see cref="Calculate"/> returns null — no made-up estimate is produced.
/// </summary>
public static class PaceCalculator
{
    private static readonly TimeSpan SoonThreshold = TimeSpan.FromMinutes(5);
    private const double MinimumHistoricalRatio = 0.03;

    public static PaceResult? Calculate(UsageWindow window, DateTimeOffset now)
    {
        // In an exhausted window a badge is shown instead of the pace; in an empty window there is no row at all.
        if (window.Percent >= 100) return null;
        if (window.Percent <= 0) return null;
        if (window.ResetsAt is null) return null;
        if (window.WindowLength is not { } length || length <= TimeSpan.Zero) return null;

        var remaining = window.ResetsAt.Value - now;
        var elapsedRatio = Math.Clamp((length - remaining) / length, 0, 1);
        if (elapsedRatio < MinimumHistoricalRatio) return null; // very new window, no reliable pace

        var used = window.Percent / 100.0;
        var tempo = used - elapsedRatio;

        var depletesIn = length * elapsedRatio * (1 / used - 1);
        if (depletesIn < TimeSpan.Zero) depletesIn = TimeSpan.Zero;

        return new PaceResult(tempo, depletesIn);
    }

    public static string Format(UsageWindow window, PaceResult pace, DateTimeOffset now)
    {
        var remaining = window.ResetsAt!.Value - now;

        if (pace.Tempo > 0.05)
        {
            // Being ahead of pace mathematically always means running out before the window ends;
            // this branch is a safety net against contradiction (floating-point dust).
            if (pace.DepletesIn >= remaining) return L.T("Comfortable · lasts until reset", "Rahat · pencere sonuna yeter");
            if (pace.DepletesIn < SoonThreshold) return L.T("Running hot · runs out soon at this pace", "Hızlı gidiyorsun · bu tempoda birazdan biter");
            return L.T($"Running hot · runs out in {FormatDuration(pace.DepletesIn)} at this pace", $"Hızlı gidiyorsun · bu tempoda {FormatDuration(pace.DepletesIn)} sonra biter");
        }

        if (pace.Tempo < -0.05) return L.T("Comfortable · lasts until reset", "Rahat · pencere sonuna yeter");

        return L.T("On pace", "Dengeli tempo");
    }

    /// <summary>Badge text shown instead of the pace in a 100% window.</summary>
    public static string FormatConsumedBadge(UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } reset) return L.T("Used up", "Tükendi");
        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero) return L.T("Used up · reset time reached — verifying", "Tükendi · sıfırlanma zamanı geldi — doğrulanıyor");
        return L.T($"Used up · resets in {FormatDuration(remaining)}", $"Tükendi · {FormatDuration(remaining)} sonra sıfırlanır");
    }

    /// <summary>"53m", "2h 10m", "2d 1h" — writes durations compactly.</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalHours < 1) return L.T($"{(int)span.TotalMinutes}m", $"{(int)span.TotalMinutes} dk");

        if (span.TotalDays < 1)
        {
            var hours = (int)span.TotalHours;
            return span.Minutes > 0 ? L.T($"{hours}h {span.Minutes}m", $"{hours} sa {span.Minutes} dk") : L.T($"{hours}h", $"{hours} sa");
        }

        var days = (int)span.TotalDays;
        return span.Hours > 0 ? L.T($"{days}d {span.Hours}h", $"{days} gün {span.Hours} sa") : L.T($"{days}d", $"{days} gün");
    }
}
