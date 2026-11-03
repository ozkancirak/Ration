using Ration.Core.Model;
using Ration.Core.Providers.Claude;
using Ration.Core.Providers.Codex;
using Ration.Core.Usage;

namespace Ration.Tests;

/// <summary>Pace calculation tests. All data is synthetic; no credentials file is read.</summary>
public class PaceCalculatorTests
{
    private static UsageWindow Window(double percent, TimeSpan length, TimeSpan remainingFromNow)
    {
        var now = DateTimeOffset.UtcNow;
        return new UsageWindow(
            WindowKind.Session, percent, 100, percent,
            now.Add(remainingFromNow), "5 saatlik", length);
    }

    private static string Line(UsageWindow window)
    {
        var now = DateTimeOffset.UtcNow;
        var pace = PaceCalculator.Calculate(window, now);
        Assert.NotNull(pace);
        return PaceCalculator.Format(window, pace!, now);
    }

    [Fact]
    public void BehindPace_ShowsComfortable()
    {
        // 1 hour of the 5-hour window has passed (20%), usage 2% → pace −0.18.
        Assert.Equal(
            "Rahat · pencere sonuna yeter",
            Line(Window(2, TimeSpan.FromHours(5), TimeSpan.FromHours(4))));
    }

    [Fact]
    public void AheadOfPace_ShowsTimeUntilDepletion()
    {
        // 8 hours of the 10-hour window have passed (80%), usage 90% → pace +0.10,
        // depletion ≈ 53 min < 2 h to reset.
        Assert.Equal(
            "Hızlı gidiyorsun · bu tempoda 53 dk sonra biter",
            Line(Window(90, TimeSpan.FromHours(10), TimeSpan.FromHours(2))));
    }

    [Fact]
    public void ImminentDepletion_SaysShortlyAndNeverZeroMinutes()
    {
        // 4 hours of the 5-hour window have passed (80%), usage 98% → depletion in under ≈ 5 min.
        var text = Line(Window(98, TimeSpan.FromHours(5), TimeSpan.FromHours(1)));

        Assert.Equal("Hızlı gidiyorsun · bu tempoda birazdan biter", text);
        Assert.DoesNotContain("0 dk", text);
    }

    [Fact]
    public void DepletionAfterWindowEnd_ShowsComfortableWithoutContradiction()
    {
        // Synthetic edge case: pace is ahead but the estimate is later than the window end → comfortable.
        var now = DateTimeOffset.UtcNow;
        var window = Window(60, TimeSpan.FromHours(10), TimeSpan.FromHours(1));
        var pace = new PaceResult(0.10, TimeSpan.FromHours(2));

        Assert.Equal("Rahat · pencere sonuna yeter", PaceCalculator.Format(window, pace, now));
    }

    [Fact]
    public void OnPace_ShowsBalanced()
    {
        // Usage exactly on time: 50% / 50% → pace 0.
        Assert.Equal(
            "Dengeli tempo",
            Line(Window(50, TimeSpan.FromHours(10), TimeSpan.FromHours(5))));
    }

    [Theory]
    [InlineData(100)]   // used up → badge, no pace row
    [InlineData(140)]   // an unclamped raw value hides it too
    [InlineData(0)]     // no usage at all
    public void UsedUpOrUnused_ShowsNoPaceRow(double percent)
    {
        Assert.Null(PaceCalculator.Calculate(
            Window(percent, TimeSpan.FromHours(5), TimeSpan.FromHours(1)), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void UsedUpBadge_ShowsRemainingTime()
    {
        var now = DateTimeOffset.UtcNow;
        var window = Window(100, TimeSpan.FromHours(5), TimeSpan.FromHours(4) + TimeSpan.FromMinutes(59));

        Assert.Equal("Tükendi · 4 sa 59 dk sonra sıfırlanır", PaceCalculator.FormatConsumedBadge(window, now));
    }

    [Fact]
    public void UsedUpBadge_WithoutResetTime_IsPlain()
    {
        var window = new UsageWindow(WindowKind.Session, 100, 100, 100, null, "5 saatlik", TimeSpan.FromHours(5));

        Assert.Equal("Tükendi", PaceCalculator.FormatConsumedBadge(window, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MissingData_ShowsNoRow(bool withReset, bool withLength)
    {
        var now = DateTimeOffset.UtcNow;
        var window = new UsageWindow(
            WindowKind.Session, 90, 100, 90,
            withReset ? now.AddHours(1) : null,
            "5 saatlik",
            withLength ? TimeSpan.FromHours(5) : null);

        Assert.Null(PaceCalculator.Calculate(window, now));
    }

    [Fact]
    public void JustResetWindow_ShowsNoRow()
    {
        // Remaining time is equal to or greater than the window length → elapsed ratio <= 0.
        Assert.Null(PaceCalculator.Calculate(
            Window(90, TimeSpan.FromHours(5), TimeSpan.FromHours(6)), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void VeryNewWindow_WithInsufficientElapsedRatio_ShowsNoRow()
    {
        Assert.Null(PaceCalculator.Calculate(
            Window(90, TimeSpan.FromHours(5), TimeSpan.FromHours(4) + TimeSpan.FromMinutes(52)),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Duration_Formatting_IsCompact()
    {
        Assert.Equal("5 dk", PaceCalculator.FormatDuration(TimeSpan.FromMinutes(5)));
        Assert.Equal("53 dk", PaceCalculator.FormatDuration(TimeSpan.FromMinutes(53)));
        Assert.Equal("2 sa 10 dk", PaceCalculator.FormatDuration(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(10)));
        Assert.Equal("3 sa", PaceCalculator.FormatDuration(TimeSpan.FromHours(3)));
        Assert.Equal("2 gün 1 sa", PaceCalculator.FormatDuration(TimeSpan.FromDays(2) + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ClaudeParser_FillsWindowLength()
    {
        const string json = """{ "five_hour": { "utilization": 10 }, "seven_day": 20 }""";

        var windows = ClaudeUsageParser.ParseWindows(json);

        Assert.Equal(TimeSpan.FromHours(5), Assert.Single(windows, w => w.Kind == WindowKind.Session).WindowLength);
        Assert.All(windows.Where(w => w.Kind == WindowKind.Weekly), w => Assert.Equal(TimeSpan.FromDays(7), w.WindowLength));
    }

    [Fact]
    public void CodexParser_ReadsWindowLengthFromResponse()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window":   { "used_percent": 10, "limit_window_seconds": 18000 },
            "secondary_window": { "used_percent": 20, "limit_window_seconds": 604800 }
          }
        }
        """;

        var windows = CodexUsageParser.ParseWindows(json);

        Assert.Equal(TimeSpan.FromHours(5), Assert.Single(windows, w => w.Kind == WindowKind.Session).WindowLength);
        Assert.Equal(TimeSpan.FromDays(7), Assert.Single(windows, w => w.Kind == WindowKind.Weekly).WindowLength);
    }

    private static UsageWindow Monthly(double percent, TimeSpan remaining, DateTimeOffset now, TimeSpan? length = null) =>
        new(WindowKind.Monthly, percent, 100, percent, now + remaining, "Monthly", length ?? TimeSpan.FromDays(30));

    [Fact]
    public void DailyTarget_DividesTheRemainingAllowanceByTheDaysLeft()
    {
        var now = DateTimeOffset.UtcNow;

        // 40% used, 10 days to the reset: 60% left over 10 days.
        Assert.Equal(6.0, PaceCalculator.DailyTarget(Monthly(40, TimeSpan.FromDays(10), now), now)!.Value, 6);
    }

    [Fact]
    public void DailyTarget_OnTheLastDay_IsTheWholeRemainder()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(30.0, PaceCalculator.DailyTarget(Monthly(70, TimeSpan.FromHours(6), now), now)!.Value, 6);
    }

    [Theory]
    [InlineData(5 * 60 * 60)]
    [InlineData(7 * 24 * 60 * 60)]
    public void DailyTarget_ShortWindows_HaveNone(int lengthSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var window = Monthly(40, TimeSpan.FromHours(2), now, TimeSpan.FromSeconds(lengthSeconds));

        Assert.Null(PaceCalculator.DailyTarget(window, now));
    }

    [Fact]
    public void DailyTarget_UsedUpOrPastTheReset_HasNone()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(PaceCalculator.DailyTarget(Monthly(100, TimeSpan.FromDays(3), now), now));
        Assert.Null(PaceCalculator.DailyTarget(Monthly(40, TimeSpan.FromDays(-1), now), now));
    }

    [Fact]
    public void DailyTarget_WithoutResetTimeOrLength_HasNone()
    {
        var now = DateTimeOffset.UtcNow;
        var noReset = new UsageWindow(WindowKind.Monthly, 40, 100, 40, null, "Monthly", TimeSpan.FromDays(30));
        var noLength = new UsageWindow(WindowKind.Monthly, 40, 100, 40, now.AddDays(5), "Monthly", null);

        Assert.Null(PaceCalculator.DailyTarget(noReset, now));
        Assert.Null(PaceCalculator.DailyTarget(noLength, now));
    }

    [Fact]
    public void DailyTarget_Formatting_RoundsToOneDecimal()
    {
        Assert.Equal("Günlük hedef ≈ %6", PaceCalculator.FormatDailyTarget(6.0));
    }
}
