using Ration.Core.Abstractions;
using Ration.Core.Model;
using Ration.Core.Usage;

namespace Ration.Tests;

public sealed class QuotaAlertsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snap(ProviderStatus status, params UsageWindow[] windows) =>
        new("claude", windows, null, null, status, SourceKind.LocalFile, Now, null);

    private static UsageWindow Window(WindowKind kind, double used, DateTimeOffset? resets, string? label = null) =>
        new(kind, used, 100, used, resets, label);

    [Fact]
    public void Thresholds_RunningLowAndUsedUp_AndAComfortableWindowIsSilent()
    {
        var alerts = QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok,
                Window(WindowKind.Session, 85, Now.AddHours(2)),
                Window(WindowKind.Weekly, 100, Now.AddDays(3)),
                Window(WindowKind.Weekly, 50, Now.AddDays(3), "Haftalık · Opus")),
            "Claude", new HashSet<string>(), Now);

        Assert.Equal(2, alerts.Count);
        Assert.Equal("Claude · Oturum kotası azaldı", alerts[0].Title);
        Assert.Equal("%15 kaldı · 2 sa 0 dk sonra sıfırlanır.", alerts[0].Body);
        Assert.Equal("Claude · Haftalık kotası doldu", alerts[1].Title);
    }

    [Fact]
    public void CustomThreshold_WarnsEarlier_AndADifferentThresholdProducesADifferentKey()
    {
        var snapshot = Snap(ProviderStatus.Ok, Window(WindowKind.Session, 75, Now.AddHours(2)));

        Assert.Empty(QuotaAlerts.Evaluate(snapshot, "Claude", new HashSet<string>(), Now));

        var atThirty = QuotaAlerts.Evaluate(snapshot, "Claude", new HashSet<string>(), Now, lowRemaining: 30);
        var alert = Assert.Single(atThirty);
        Assert.EndsWith("|30", alert.Key);
        Assert.Equal("%25 kaldı · 2 sa 0 dk sonra sıfırlanır.", alert.Body);
    }

    [Fact]
    public void SameCycle_DoesNotRepeat_EvenWithADriftingResetTime()
    {
        var first = QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 90, Now.AddHours(2))),
            "Claude", new HashSet<string>(), Now);
        var fired = first.Select(a => a.Key).ToHashSet();

        // Same cycle, the reset time has drifted by 7 s (sources computed from "in N seconds").
        var again = QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 92, Now.AddHours(2).AddSeconds(7))),
            "Claude", fired, Now);

        Assert.Single(first);
        Assert.Empty(again);
    }

    [Fact]
    public void NewCycle_NewNotification()
    {
        var fired = QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 90, Now.AddHours(2))),
            "Claude", new HashSet<string>(), Now).Select(a => a.Key).ToHashSet();

        var next = QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 90, Now.AddHours(7))),
            "Claude", fired, Now);

        Assert.Single(next);
    }

    [Fact]
    public void StaleData_AndPerModelLimit_ProduceNoNotification()
    {
        Assert.Empty(QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Degraded, Window(WindowKind.Session, 99, Now.AddHours(1))),
            "Claude", new HashSet<string>(), Now));

        Assert.Empty(QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Weekly, 100, Now.AddDays(1), "gpt-reserve · Haftalık")),
            "Codex", new HashSet<string>(), Now));
    }
}
