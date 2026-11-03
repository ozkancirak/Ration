using Ration.Core.Model;
using Ration.Core.Usage;

namespace Ration.Tests;

public class SurgeTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Make(double percent, ProviderStatus status = ProviderStatus.Ok, string provider = "claude", WindowKind kind = WindowKind.Session, string? label = null) =>
        new(provider, [new UsageWindow(kind, percent, 100, percent, null, label)], null, null, status, null, T0, null);

    [Fact]
    public void FirstSample_IsOnlyABaseline()
    {
        Assert.Empty(new SurgeTracker().Observe(Make(40), "Claude", T0));
    }

    [Fact]
    public void FastJump_Alerts_WithTheJumpTheTimeAndWhatIsLeft()
    {
        var tracker = new SurgeTracker();
        tracker.Observe(Make(40), "Claude", T0);

        var alert = Assert.Single(tracker.Observe(Make(52), "Claude", T0.AddMinutes(5)));

        Assert.Equal("Claude · Oturum kullanımı hızla artıyor", alert.Title);
        Assert.Equal("5 dk'da +%12 · %48 kaldı", alert.Body);
    }

    [Theory]
    [InlineData(12, 30)]   // 2.0 points per 5 minutes is a normal pace
    [InlineData(29, 15)]   // just under 10 points per 5 minutes
    [InlineData(4, 1)]     // a fast but tiny move is noise
    [InlineData(40, 61)]   // too far apart to say anything about "quickly"
    public void SlowOrTinyMoves_DoNotAlert(double jump, int minutes)
    {
        var tracker = new SurgeTracker();
        tracker.Observe(Make(10), "Claude", T0);

        Assert.Empty(tracker.Observe(Make(10 + jump), "Claude", T0.AddMinutes(minutes)));
    }

    [Fact]
    public void RefreshesSecondsApart_AddUpInsteadOfBeingCutIntoSmallPieces()
    {
        var tracker = new SurgeTracker();
        tracker.Observe(Make(10), "Claude", T0);

        Assert.Empty(tracker.Observe(Make(14), "Claude", T0.AddSeconds(30)));
        Assert.Single(tracker.Observe(Make(18), "Claude", T0.AddMinutes(3)));
    }

    [Fact]
    public void AfterAnAlert_TheWindowStaysQuietForAWhile()
    {
        var tracker = new SurgeTracker();
        tracker.Observe(Make(10), "Claude", T0);
        Assert.Single(tracker.Observe(Make(25), "Claude", T0.AddMinutes(5)));

        Assert.Empty(tracker.Observe(Make(40), "Claude", T0.AddMinutes(10)));
        // 16 minutes after the alert the quiet time is over: +30 in 11 minutes is still a surge.
        Assert.Single(tracker.Observe(Make(70), "Claude", T0.AddMinutes(21)));
    }

    [Fact]
    public void ADrop_IsAResetAndSetsANewBaseline()
    {
        var tracker = new SurgeTracker();
        tracker.Observe(Make(95), "Claude", T0);

        Assert.Empty(tracker.Observe(Make(2), "Claude", T0.AddMinutes(10)));
        Assert.Single(tracker.Observe(Make(20), "Claude", T0.AddMinutes(14)));
    }

    [Fact]
    public void SensitivityIsConfigurable()
    {
        var tracker = new SurgeTracker();
        tracker.Observe(Make(10), "Claude", T0);

        // +6 in 5 minutes: below the default of 10, above a sensitive 5.
        Assert.Empty(tracker.Observe(Make(16), "Claude", T0.AddMinutes(5), pointsPerFiveMinutes: 10));
        Assert.Single(tracker.Observe(Make(22), "Claude", T0.AddMinutes(10), pointsPerFiveMinutes: 5));
    }

    [Fact]
    public void OnlyFreshMainWindows_AreWatched_EachOnItsOwn()
    {
        var tracker = new SurgeTracker();
        tracker.Observe(Make(10), "Claude", T0);
        tracker.Observe(Make(10, provider: "codex"), "Codex", T0);
        tracker.Observe(Make(10, kind: WindowKind.Weekly, label: "gpt-reserve · Weekly"), "Codex", T0);

        // Stale data is ignored and does not move the baseline.
        Assert.Empty(tracker.Observe(Make(90, ProviderStatus.Degraded), "Claude", T0.AddMinutes(2)));
        Assert.Single(tracker.Observe(Make(30), "Claude", T0.AddMinutes(4)));

        // Another provider and the per-model extra limit are tracked separately / not at all.
        Assert.Empty(tracker.Observe(Make(11, provider: "codex"), "Codex", T0.AddMinutes(4)));
        Assert.Empty(tracker.Observe(Make(90, kind: WindowKind.Weekly, label: "gpt-reserve · Weekly"), "Codex", T0.AddMinutes(4)));
    }
}
