using Ration.Core.Cost;
using Ration.Core.Model;
using Ration.Core.Providers;
using Ration.Core.Providers.Copilot;
using Ration.Core.Providers.Status;

namespace Ration.Tests;

/// <summary>
/// The session logs here are SYNTHETIC and follow the documented shape of session.shutdown events.
/// Conversation content copied from a real Copilot session cannot enter the repo.
/// </summary>
public sealed class CopilotTests : IDisposable
{
    private static readonly DateTimeOffset Since = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ration-copilot-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string WriteSession(string session, params string[] lines)
    {
        var directory = Path.Combine(_root, "session-state", session);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "events.jsonl"), lines);
        return Path.Combine(_root, "session-state");
    }

    private static string Shutdown(string timestamp, string model, long input, long output, long cacheRead, long cacheWrite, long reasoning = 0) =>
        "{\"type\":\"session.shutdown\",\"timestamp\":\"" + timestamp + "\",\"data\":{\"modelMetrics\":{\"" + model + "\":{\"requests\":{\"count\":3},\"usage\":{"
        + "\"inputTokens\":" + input + ",\"outputTokens\":" + output + ",\"cacheReadTokens\":" + cacheRead + ",\"cacheWriteTokens\":" + cacheWrite
        + ",\"reasoningTokens\":" + reasoning + "}}}}}";

    [Fact]
    public void FirstShutdown_CountsAsReported_WithTheCacheBucketsTakenOutOfTheInput()
    {
        var directory = WriteSession("s1", Shutdown("2026-09-20T10:00:00Z", "claude-opus-4.7", 100, 50, 10, 20, 5));

        var scan = CopilotCostScanner.Scan(Since, directory);

        var model = Assert.Single(scan.Tally.Models);
        Assert.Equal("claude-opus-4-7", model.Model);
        Assert.Equal(70, model.InputTokens);
        Assert.Equal(50, model.OutputTokens);
        Assert.Equal(10, model.CacheReadTokens);
        Assert.Equal(20, model.CacheCreationTokens);
        Assert.Equal(5, model.ReasoningTokens);
        Assert.Equal(1, scan.FilesScanned);
        Assert.True(scan.PeriodKnown);
    }

    [Fact]
    public void ResumedSession_CountsTheDifferenceFromThePreviousSnapshot()
    {
        var directory = WriteSession("s1",
            Shutdown("2026-09-20T10:00:00Z", "gpt-5", 100, 50, 0, 0),
            Shutdown("2026-09-22T10:00:00Z", "gpt-5", 160, 90, 0, 0));

        var scan = CopilotCostScanner.Scan(Since, directory);

        // The totals match the last snapshot, and the usage follows the resume date.
        var model = Assert.Single(scan.Tally.Models);
        Assert.Equal(160, model.InputTokens);
        Assert.Equal(90, model.OutputTokens);
        Assert.Equal([150L, 100L], scan.Tally.Daily.Select(d => d.Tokens));
    }

    [Fact]
    public void SnapshotsBeforeThePeriod_StayAsTheBaseline()
    {
        var directory = WriteSession("s1",
            Shutdown("2026-08-20T10:00:00Z", "gpt-5", 100, 50, 0, 0),
            Shutdown("2026-09-22T10:00:00Z", "gpt-5", 160, 90, 0, 0));

        var model = Assert.Single(CopilotCostScanner.Scan(Since, directory).Tally.Models);

        Assert.Equal(60, model.InputTokens);
        Assert.Equal(40, model.OutputTokens);
    }

    [Fact]
    public void CountersThatGoDown_MeanTheTotalsRestarted()
    {
        var directory = WriteSession("s1",
            Shutdown("2026-09-20T10:00:00Z", "gpt-5", 100, 50, 0, 0),
            Shutdown("2026-09-22T10:00:00Z", "gpt-5", 30, 10, 0, 0));

        var model = Assert.Single(CopilotCostScanner.Scan(Since, directory).Tally.Models);

        Assert.Equal(130, model.InputTokens);
        Assert.Equal(60, model.OutputTokens);
    }

    [Fact]
    public void SessionsAndModels_AreCountedSeparately()
    {
        WriteSession("s1", Shutdown("2026-09-20T10:00:00Z", "gpt-5", 100, 50, 0, 0));
        var directory = WriteSession("s2",
            Shutdown("2026-09-21T10:00:00Z", "gpt-5", 40, 20, 0, 0),
            "{\"type\":\"session.shutdown\",\"timestamp\":\"2026-09-21T11:00:00Z\",\"data\":{\"modelMetrics\":{\"claude-sonnet-4.5-1m\":{\"usage\":{\"inputTokens\":7,\"outputTokens\":3}}}}}");

        var scan = CopilotCostScanner.Scan(Since, directory);

        Assert.Equal(2, scan.FilesScanned);
        Assert.Equal(140, scan.Tally.Models.Single(m => m.Model == "gpt-5").InputTokens);
        Assert.Equal(7, scan.Tally.Models.Single(m => m.Model == "claude-sonnet-4-5").InputTokens);
    }

    [Fact]
    public void OtherLines_AreSkipped_AndCorruptOnesDoNotStopTheScan()
    {
        var directory = WriteSession("s1",
            "{\"type\":\"assistant.message\",\"timestamp\":\"2026-09-20T09:00:00Z\",\"data\":{\"content\":\"SECRET conversation text\"}}",
            "this is not json at all",
            "{\"type\":\"session.shutdown\",\"timestamp\":\"2026-09-20T09",
            "{\"type\":\"session.shutdown\",\"data\":{}}",
            "{\"type\":\"session.shutdown\",\"timestamp\":\"2026-09-20T10:00:00Z\",\"data\":{\"modelMetrics\":{\"gpt-5\":{\"usage\":{\"inputTokens\":5,\"outputTokens\":2}},\"broken\":3}}}");

        var model = Assert.Single(CopilotCostScanner.Scan(Since, directory).Tally.Models);

        Assert.Equal(5, model.InputTokens);
        Assert.Equal(2, model.OutputTokens);
    }

    [Fact]
    public void MissingTimestamp_KeepsTheCount_ButTheCalendarPeriodIsNotExact()
    {
        var directory = WriteSession("s1",
            "{\"type\":\"session.shutdown\",\"data\":{\"modelMetrics\":{\"gpt-5\":{\"usage\":{\"inputTokens\":9,\"outputTokens\":1}}}}}");

        var scan = CopilotCostScanner.Scan(Since, directory);

        Assert.Equal(9, Assert.Single(scan.Tally.Models).InputTokens);
        Assert.False(scan.PeriodKnown);
        Assert.Empty(scan.Tally.Daily);
    }

    [Fact]
    public void MissingDirectory_GivesAnEmptyResultWithANote()
    {
        var scan = CopilotCostScanner.Scan(Since, Path.Combine(_root, "nowhere"));

        Assert.True(scan.Tally.IsEmpty);
        Assert.NotNull(scan.Note);
        Assert.Equal(0, scan.FilesScanned);
    }

    [Theory]
    [InlineData("claude-opus-4.6-1m", "claude-opus-4-6")]
    [InlineData("claude-opus-4.7-1m-internal", "claude-opus-4-7")]
    [InlineData("claude-sonnet-4.5", "claude-sonnet-4-5")]
    [InlineData("gpt-5.1", "gpt-5.1")]
    [InlineData("gpt-5-1m", "gpt-5")]
    [InlineData("  Claude-Haiku-4.5  ", "Claude-Haiku-4-5")]
    public void ModelNames_AreNormalizedForPricing(string model, string expected)
    {
        Assert.Equal(expected, CopilotCostScanner.NormalizeModel(model));
    }

    [Fact]
    public async Task Source_ReportsNotInstalledWithoutLogs_AndAQuotalessOkWithThem()
    {
        var missing = await new CopilotLocalSource(Path.Combine(_root, "nowhere")).FetchAsync();
        Assert.Equal(ProviderStatus.NotInstalled, missing.Status);

        var directory = WriteSession("s1", Shutdown("2026-09-20T10:00:00Z", "gpt-5", 1, 1, 0, 0));
        var found = await new CopilotLocalSource(directory).FetchAsync();

        Assert.Equal(ProviderStatus.Ok, found.Status);
        Assert.Equal("copilot", found.ProviderId);
        Assert.Empty(found.Windows);
        Assert.Equal("Deneysel", found.PlanName);
    }

    [Fact]
    public void Registry_AddsCopilotOnlyWhenItsSessionLogsExist()
    {
        var withLogs = WriteSession("s1", Shutdown("2026-09-20T10:00:00Z", "gpt-5", 1, 1, 0, 0));

        Assert.Contains(ProviderRegistry.CreateAll(new HttpClient(), copilotSessionStateDirectory: withLogs), p => p.Id == "copilot");
        Assert.DoesNotContain(
            ProviderRegistry.CreateAll(new HttpClient(), copilotSessionStateDirectory: Path.Combine(_root, "nowhere")),
            p => p.Id == "copilot");
    }

    [Fact]
    public void Copilot_HasAStatusPageFeed()
    {
        var source = ProviderStatusPages.For("copilot");

        Assert.NotNull(source);
        Assert.StartsWith("https://", source.SummaryUrl);
        Assert.Contains("Copilot", source.Components);
    }
}
