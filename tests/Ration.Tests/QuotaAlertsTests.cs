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

    private static HashSet<string> UsedUpAt(DateTimeOffset resets) =>
        QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 100, resets)),
            "Claude", new HashSet<string>(), Now).Select(a => a.Key).ToHashSet();

    [Fact]
    public void Reset_AfterAUsedUpWindow_NotifiesOnce_AndForgetsTheUsedUpKey()
    {
        var fired = UsedUpAt(Now.AddHours(1));
        var later = Now.AddHours(2);
        var fresh = Snap(ProviderStatus.Ok, Window(WindowKind.Session, 3, later.AddHours(5)));

        var reset = Assert.Single(QuotaAlerts.Evaluate(fresh, "Claude", fired, later, includeResets: true));

        Assert.EndsWith("|reset", reset.Key);
        Assert.Equal("Claude · Oturum kotası sıfırlandı", reset.Title);
        Assert.Equal("Yeni kota: %97 kaldı.", reset.Body);
        Assert.Equal(fired, reset.Forget!.ToHashSet());

        // The notifier records the key and drops the used-up one: nothing repeats in this or a later cycle.
        var after = fired.Except(reset.Forget!).Append(reset.Key).ToHashSet();
        Assert.Empty(QuotaAlerts.Evaluate(fresh, "Claude", after, later, includeResets: true));
        Assert.Empty(QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 10, later.AddHours(12))),
            "Claude", after, later.AddHours(7), includeResets: true));
    }

    [Fact]
    public void Reset_IsOffByDefault_AndNeedsAUsedUpAlertBefore()
    {
        var fresh = Snap(ProviderStatus.Ok, Window(WindowKind.Session, 3, Now.AddHours(7)));

        Assert.Empty(QuotaAlerts.Evaluate(fresh, "Claude", UsedUpAt(Now.AddHours(1)), Now.AddHours(2)));
        Assert.Empty(QuotaAlerts.Evaluate(fresh, "Claude", new HashSet<string>(), Now.AddHours(2), includeResets: true));
    }

    [Fact]
    public void Reset_NeedsANewCycle_AndAResetTime()
    {
        var fired = UsedUpAt(Now.AddHours(1));

        // Usage dropped inside the same cycle (a correction): not a reset.
        Assert.Empty(QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 40, Now.AddHours(1))),
            "Claude", fired, Now, includeResets: true));

        // A window without a reset time has no cycle to compare.
        Assert.Empty(QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 40, null)),
            "Claude", fired, Now, includeResets: true));
    }
}
