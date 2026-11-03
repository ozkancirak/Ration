using Ration.Core.Model;
using Ration.Core.Usage;

namespace Ration.Tests;

public class TrayWindowSelectorTests
{
    private static UsageWindow Window(WindowKind kind, double percent, string label) =>
        new(kind, percent, 100, percent, null, label, null);

    private static UsageSnapshot Make(params UsageWindow[] windows) =>
        new("claude", windows, null, null, ProviderStatus.Ok, null, DateTimeOffset.UtcNow, null);

    private static readonly UsageSnapshot Sample = Make(
        Window(WindowKind.Session, 40, "5-hour"),
        Window(WindowKind.Weekly, 70, "Weekly"),
        Window(WindowKind.Weekly, 90, "Weekly · Opus"),
        Window(WindowKind.Weekly, 99, "gpt-reserve · Weekly"));

    [Fact]
    public void Highest_PicksTheMostUsedMainWindow_AndIgnoresPerModelExtraLimits()
    {
        var picked = TrayWindowSelector.Pick(Sample, TrayWindowChoice.Highest);

        Assert.Equal("Weekly · Opus", picked!.Label);
    }

    [Fact]
    public void Session_PicksTheSessionWindow()
    {
        Assert.Equal("5-hour", TrayWindowSelector.Pick(Sample, TrayWindowChoice.Session)!.Label);
    }

    [Fact]
    public void Weekly_PicksTheMostUsedWeeklyWindow()
    {
        Assert.Equal("Weekly · Opus", TrayWindowSelector.Pick(Sample, TrayWindowChoice.Weekly)!.Label);
    }

    [Theory]
    [InlineData(TrayWindowChoice.Session)]
    [InlineData(TrayWindowChoice.Weekly)]
    public void MissingKind_FallsBackToTheMostUsedMainWindow(TrayWindowChoice choice)
    {
        var onlyOne = choice == TrayWindowChoice.Session
            ? Make(Window(WindowKind.Weekly, 55, "Weekly"))
            : Make(Window(WindowKind.Session, 25, "5-hour"));

        var picked = TrayWindowSelector.Pick(onlyOne, choice);

        Assert.NotNull(picked);
        Assert.Equal(onlyOne.Windows[0].Label, picked.Label);
    }

    [Fact]
    public void NoMainWindow_ReturnsNull()
    {
        var snapshot = Make(
            Window(WindowKind.Monthly, 50, "Monthly"),
            Window(WindowKind.Weekly, 60, "gpt-reserve · Weekly"));

        Assert.Null(TrayWindowSelector.Pick(snapshot, TrayWindowChoice.Highest));
        Assert.Null(TrayWindowSelector.Pick(Make(), TrayWindowChoice.Weekly));
    }
}
