using Ration.Core.Model;
using Ration.Core.Providers.Antigravity;
using Ration.Core.Providers.Claude;
using Ration.Core.Providers.Codex;

namespace Ration.Tests;

public sealed class UsagePercentScaleTests
{
    [Fact]
    public void Claude_UtilizationZatenYuzdedir()
    {
        var window = Assert.Single(
            ClaudeUsageParser.ParseWindows("""{"five_hour":{"utilization":27.0}}"""));

        Assert.Equal(27.0, window.Percent);
    }

    [Fact]
    public void Antigravity_RemainingFraction_RationaDonusur()
    {
        var window = Assert.Single(
            AntigravityUsageParser.ParseWindows(
                """{"groups":[{"buckets":[{"window":"5h","remainingFraction":0.25}]}]}"""));

        Assert.Equal(75.0, window.Percent);
    }

    [Fact]
    public void Codex_UsedPercentZatenYuzdedir()
    {
        var window = Assert.Single(
            CodexUsageParser.ParseWindows(
                """{"rate_limit":{"primary_window":{"used_percent":31.2}}}"""));

        Assert.Equal(WindowKind.Session, window.Kind);
        Assert.Equal(31.2, window.Percent);
    }
}
