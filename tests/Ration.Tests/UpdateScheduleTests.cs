using Ration.Core.Updates;

namespace Ration.Tests;

public class UpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static UpdateCheckResult Result(TimeSpan ago, bool succeeded = true) =>
        new(true, false, null, succeeded, Now - ago);

    [Fact]
    public void NeverChecked_IsDue() => Assert.True(UpdateSchedule.IsDue(null, Now));

    [Theory]
    [InlineData(1, false)]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(72, true)]
    public void SuccessfulCheck_IsDueAfterADay(int hoursAgo, bool expected)
    {
        Assert.Equal(expected, UpdateSchedule.IsDue(Result(TimeSpan.FromHours(hoursAgo)), Now));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void FailedCheck_RetriesAfterTwoHours(int hoursAgo, bool expected)
    {
        Assert.Equal(expected, UpdateSchedule.IsDue(Result(TimeSpan.FromHours(hoursAgo), succeeded: false), Now));
    }

    [Fact]
    public void ClockMovedBack_IsDue()
    {
        Assert.True(UpdateSchedule.IsDue(Result(TimeSpan.FromHours(-5)), Now));
    }
}
