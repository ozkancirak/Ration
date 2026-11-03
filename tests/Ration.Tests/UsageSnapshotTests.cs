using Ration.Core.Model;

namespace Ration.Tests;

public class UsageSnapshotTests
{
    private static UsageSnapshot Make(ProviderStatus status, string? staleReason) =>
        new("claude", [], null, null, status, null, DateTimeOffset.UtcNow, staleReason);

    [Theory]
    [InlineData(ProviderStatus.Ok, null, false)]
    [InlineData(ProviderStatus.Degraded, "From previous session", true)]
    [InlineData(ProviderStatus.Degraded, null, false)]
    [InlineData(ProviderStatus.Error, "Network error", false)]
    public void IsStale_OnlyForDegradedWithAStaleReason(ProviderStatus status, string? staleReason, bool expected)
    {
        Assert.Equal(expected, Make(status, staleReason).IsStale);
    }
}
