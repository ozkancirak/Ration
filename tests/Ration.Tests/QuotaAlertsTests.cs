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
    public void Esikler_AzaldiVeDoldu_VeRahatPencereSessiz()
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
    public void AyniDongudeTekrarlanmaz_KayanSifirlanmaZamanindaBile()
    {
        var first = QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 90, Now.AddHours(2))),
            "Claude", new HashSet<string>(), Now);
        var fired = first.Select(a => a.Key).ToHashSet();

        // Aynı döngü, sıfırlanma zamanı 7 sn kaymış ("N sn sonra"dan hesaplanan kaynaklar).
        var again = QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Session, 92, Now.AddHours(2).AddSeconds(7))),
            "Claude", fired, Now);

        Assert.Single(first);
        Assert.Empty(again);
    }

    [Fact]
    public void YeniDongu_YeniBildirim()
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
    public void BayatVeri_VeModelBazliLimit_BildirimUretmez()
    {
        Assert.Empty(QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Degraded, Window(WindowKind.Session, 99, Now.AddHours(1))),
            "Claude", new HashSet<string>(), Now));

        Assert.Empty(QuotaAlerts.Evaluate(
            Snap(ProviderStatus.Ok, Window(WindowKind.Weekly, 100, Now.AddDays(1), "gpt-reserve · Haftalık")),
            "Codex", new HashSet<string>(), Now));
    }
}
