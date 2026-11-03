using Ration.Core.Model;
using Ration.Core.Usage;

namespace Ration.Tests;

public class CredentialEpisodesTests
{
    private static UsageSnapshot Make(ProviderStatus status, string providerId = "claude") =>
        new(providerId, [], null, null, status, null, DateTimeOffset.UtcNow, null);

    [Fact]
    public void NeverWorked_IsNeverReported()
    {
        var episodes = new CredentialEpisodes([], []);

        Assert.Null(episodes.Pending(Make(ProviderStatus.AuthRequired), "Claude"));
    }

    [Fact]
    public void AfterASuccess_AnExpiredSessionIsReportedOnce_UntilAFetchSucceedsAgain()
    {
        var episodes = new CredentialEpisodes([], []);
        Assert.True(episodes.Track(Make(ProviderStatus.Ok)));

        var alert = episodes.Pending(Make(ProviderStatus.AuthRequired), "Claude");
        Assert.NotNull(alert);
        Assert.Equal("Claude için yeniden giriş gerekiyor", alert.Title);

        Assert.True(episodes.MarkNotified("claude"));
        Assert.Null(episodes.Pending(Make(ProviderStatus.AuthRequired), "Claude"));

        // Errors and stale data do not end the episode; only a successful fetch does.
        Assert.False(episodes.Track(Make(ProviderStatus.Error)));
        Assert.False(episodes.Track(Make(ProviderStatus.Degraded)));
        Assert.Null(episodes.Pending(Make(ProviderStatus.AuthRequired), "Claude"));

        Assert.True(episodes.Track(Make(ProviderStatus.Ok)));
        Assert.NotNull(episodes.Pending(Make(ProviderStatus.AuthRequired), "Claude"));
    }

    [Fact]
    public void OtherStatuses_ProduceNoAlert()
    {
        var episodes = new CredentialEpisodes(["claude"], []);

        foreach (var status in new[] { ProviderStatus.Ok, ProviderStatus.Degraded, ProviderStatus.Error, ProviderStatus.NotInstalled })
        {
            Assert.Null(episodes.Pending(Make(status), "Claude"));
        }
    }

    [Fact]
    public void State_SurvivesARestart_AndIsPerProvider()
    {
        var episodes = new CredentialEpisodes(["claude", "codex"], ["codex"]);

        Assert.NotNull(episodes.Pending(Make(ProviderStatus.AuthRequired, "claude"), "Claude"));
        Assert.Null(episodes.Pending(Make(ProviderStatus.AuthRequired, "codex"), "Codex"));
        Assert.Equal(["claude", "codex"], episodes.EverWorked.Order());
    }
}
