using Ration.Core;
using Ration.Core.Abstractions;
using Ration.Core.Model;
using Ration.Core.Usage;

namespace Ration.Tests;

// The language is a single process-wide flag; it must not be changed while other tests expect Turkish.
[CollectionDefinition(nameof(LanguageTests), DisableParallelization = true)]
public sealed class LanguageCollection;

/// <summary>English texts. The other tests run in Turkish (TestLogIsolation).</summary>
[Collection(nameof(LanguageTests))]
public sealed class LanguageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static T InEnglish<T>(Func<T> action)
    {
        L.Turkish = false;
        try { return action(); }
        finally { L.Turkish = true; }
    }

    [Fact]
    public void Tag_RoundTrips()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            Assert.Equal(language, L.FromTag(L.ToTag(language)));
        }
        Assert.Equal(AppLanguage.System, L.FromTag("bozuk"));
        Assert.True(L.Resolve(AppLanguage.Turkish));
        Assert.False(L.Resolve(AppLanguage.English));
    }

    [Fact]
    public void Notification_InEnglish()
    {
        var alerts = InEnglish(() => QuotaAlerts.Evaluate(
            new UsageSnapshot("claude",
                [
                    new UsageWindow(WindowKind.Session, 85, 100, 85, Now.AddHours(2)),
                    new UsageWindow(WindowKind.Weekly, 100, 100, 100, Now.AddDays(3)),
                ],
                null, null, ProviderStatus.Ok, SourceKind.LocalFile, Now, null),
            "Claude", new HashSet<string>(), Now));

        Assert.Equal("Claude · Session quota running low", alerts[0].Title);
        Assert.Equal("15% left · resets in 2h 0m.", alerts[0].Body);
        Assert.Equal("Claude · Weekly quota used up", alerts[1].Title);
    }

    [Fact]
    public void Pace_InEnglish()
    {
        var window = new UsageWindow(
            WindowKind.Session, 100, 100, 100, Now.AddHours(2), "5-hour", TimeSpan.FromHours(5));
        Assert.Equal("Used up · resets in 2h", InEnglish(() => PaceCalculator.FormatConsumedBadge(window, Now)));
        Assert.Equal("2d 1h", InEnglish(() => PaceCalculator.FormatDuration(TimeSpan.FromHours(49))));
    }
}
