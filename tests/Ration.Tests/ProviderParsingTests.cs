using Ration.Core.Model;
using Ration.Core.Providers.Claude;
using Ration.Core.Providers.Codex;

namespace Ration.Tests;

/// <summary>
/// The JSON here is ENTIRELY SYNTHETIC. Nothing copied from the contents of a real
/// .credentials.json / auth.json can enter the repo.
/// </summary>
public class ProviderParsingTests
{
    [Fact]
    public void ClaudeParser_ReadsObjectShapedWindows()
    {
        const string json = """
        {
          "five_hour":      { "utilization": 42.5, "resets_at": "2026-09-19T21:00:00Z" },
          "seven_day":      { "utilization": 18,   "resets_at": "2026-09-24T00:00:00Z" },
          "seven_day_opus": { "utilization": 5 }
        }
        """;

        var windows = ClaudeUsageParser.ParseWindows(json);

        Assert.Equal(3, windows.Count);

        var session = Assert.Single(windows, w => w.Kind == WindowKind.Session);
        Assert.Equal(42.5, session.Percent);
        Assert.NotNull(session.ResetsAt);
        Assert.Equal("5 saatlik", session.Label);

        Assert.Equal(2, windows.Count(w => w.Kind == WindowKind.Weekly));
    }

    [Fact]
    public void ClaudeParser_ReadsPlainNumberWindows()
    {
        const string json = """{ "five_hour": 73, "seven_day": 12 }""";

        var windows = ClaudeUsageParser.ParseWindows(json);

        Assert.Equal(2, windows.Count);
        Assert.Equal(73, windows[0].Percent);
        Assert.Null(windows[0].ResetsAt);
    }

    [Fact]
    public void ClaudeParser_SkipsUnknownFields_DoesNotMakeUpData()
    {
        const string json = """{ "bambaska_bir_alan": { "utilization": 99 } }""";

        Assert.Empty(ClaudeUsageParser.ParseWindows(json));
    }

    [Fact]
    public void ClaudeParser_ClampsOutOfRangePercent()
    {
        const string json = """{ "five_hour": 140, "seven_day": -5 }""";

        var windows = ClaudeUsageParser.ParseWindows(json);

        Assert.Equal(100, windows[0].Percent);
        Assert.Equal(0, windows[1].Percent);
    }

    [Fact]
    public void ClaudeParser_TellsEpochSecondsFromMilliseconds()
    {
        const string json = """
        {
          "five_hour": { "utilization": 10, "resets_at": 1789838400 },
          "seven_day": { "utilization": 10, "resets_at": 1789838400000 }
        }
        """;

        var windows = ClaudeUsageParser.ParseWindows(json);

        Assert.Equal(windows[0].ResetsAt, windows[1].ResetsAt);
    }

    // The format is a synthetic copy simplified from a real response observed in July 2026.
    private const string ScopedLimitsJson = """
    {
      "five_hour": { "utilization": 10, "resets_at": "2026-07-08T22:59:59Z" },
      "seven_day": { "utilization": 55, "resets_at": "2026-07-10T10:59:59Z" },
      "seven_day_opus": null,
      "seven_day_sonnet": null,
      "limits": [
        { "kind": "session",       "group": "session", "percent": 10, "scope": null },
        { "kind": "weekly_all",    "group": "weekly",  "percent": 55, "scope": null },
        { "kind": "weekly_scoped", "group": "weekly",  "percent": 84, "resets_at": "2026-07-10T10:59:59Z",
          "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
      ]
    }
    """;

    [Fact]
    public void ClaudeParser_ReadsTheModelScopedWeeklyWindowFromTheLimitsArray()
    {
        var windows = ClaudeUsageParser.ParseWindows(ScopedLimitsJson);

        // The session and weekly_all entries repeat five_hour / seven_day, they are not added again.
        Assert.Equal(3, windows.Count);
        var fable = Assert.Single(windows, w => w.Label?.EndsWith("Fable", StringComparison.Ordinal) == true);
        Assert.Equal(WindowKind.Weekly, fable.Kind);
        Assert.Equal(84, fable.Percent);
        Assert.Equal(TimeSpan.FromDays(7), fable.WindowLength);
        Assert.NotNull(fable.ResetsAt);
    }

    [Fact]
    public void ClaudeParser_DropsALimitsEntryWithoutPercent_DoesNotInventZero()
    {
        const string json = """
        {
          "limits": [
            { "kind": "weekly_scoped", "scope": { "model": { "display_name": "Fable" } } },
            { "kind": "weekly_scoped", "percent": null, "scope": { "model": { "display_name": "Design" } } },
            { "kind": "weekly_scoped", "percent": 40, "scope": { "model": { "display_name": "  " } } },
            { "kind": "weekly_scoped", "percent": 40, "scope": null },
            { "kind": "weekly_scoped", "percent": 40 },
            "bozuk"
          ]
        }
        """;

        Assert.Empty(ClaudeUsageParser.ParseWindows(json));
    }

    [Fact]
    public void ClaudeParser_OldFieldAndLimits_DoNotAddTheSameModelTwice()
    {
        const string json = """
        {
          "seven_day_opus": { "utilization": 30 },
          "limits": [
            { "kind": "weekly_scoped", "percent": 30, "scope": { "model": { "display_name": "Opus" } } },
            { "kind": "weekly_scoped", "percent": 12, "scope": { "model": { "display_name": "Sonnet" } } }
          ]
        }
        """;

        var windows = ClaudeUsageParser.ParseWindows(json);

        Assert.Equal(2, windows.Count);
        Assert.Single(windows, w => w.Label?.EndsWith("Opus", StringComparison.Ordinal) == true);
        Assert.Single(windows, w => w.Label?.EndsWith("Sonnet", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ClaudeParser_IgnoresLimitsThatIsNotAnArray()
    {
        const string json = """{ "five_hour": 5, "limits": { "kind": "weekly_scoped" } }""";

        Assert.Single(ClaudeUsageParser.ParseWindows(json));
    }

    [Fact]
    public void CodexParser_ReadsPrimaryAndSecondaryWindows()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window":   { "used_percent": 31.2, "resets_in_seconds": 3600 },
            "secondary_window": { "used_percent": 7.5,  "resets_at": "2026-09-26T00:00:00Z" }
          }
        }
        """;

        var windows = CodexUsageParser.ParseWindows(json);

        Assert.Equal(2, windows.Count);

        var session = Assert.Single(windows, w => w.Kind == WindowKind.Session);
        Assert.Equal(31.2, session.Percent);
        Assert.NotNull(session.ResetsAt);

        var weekly = Assert.Single(windows, w => w.Kind == WindowKind.Weekly);
        Assert.Equal(7.5, weekly.Percent);
    }

    [Fact]
    public void CodexParser_WithoutARateLimitWrapper_LooksAtTheRoot()
    {
        const string json = """{ "primary_window": { "used_percent": 10 } }""";

        var window = Assert.Single(CodexUsageParser.ParseWindows(json));

        Assert.Equal(WindowKind.Session, window.Kind);
        Assert.Equal(10, window.Percent);
    }

    [Fact]
    public void CredentialReaders_ReturnNullForAMissingFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"ration-yok-{Guid.NewGuid():N}.json");

        Assert.Null(ClaudeCredentialStore.TryRead(missing));
        Assert.Null(CodexCredentialStore.TryRead(missing));
    }

    [Fact]
    public void ClaudeCredentials_ReturnNullWithoutClaudeAiOauth()
    {
        // In Claude Code 2.1.x the file may contain only mcpOAuth; that is not usable.
        var path = Path.Combine(Path.GetTempPath(), $"ration-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "mcpOAuth": { "accessToken": "sentetik-deger" } }""");

        try
        {
            Assert.Null(ClaudeCredentialStore.TryRead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClaudeCredentials_ReadAValidFile_AndFlagAnExpiredOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ration-test-{Guid.NewGuid():N}.json");
        var gecmisZaman = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();

        File.WriteAllText(path, $$"""
        {
          "claudeAiOauth": {
            "accessToken": "sentetik-token",
            "refreshToken": "sentetik-refresh-token",
            "expiresAt": {{gecmisZaman}},
            "subscriptionType": "max"
          }
        }
        """);

        try
        {
            var credentials = ClaudeCredentialStore.TryRead(path);

            Assert.NotNull(credentials);
            Assert.Equal("max", credentials!.SubscriptionType);
            Assert.Equal("sentetik-refresh-token", credentials.RefreshToken);
            Assert.True(credentials.IsExpired);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CodexCredentials_ReadTheTokensSection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ration-test-{Guid.NewGuid():N}.json");

        File.WriteAllText(path, """
        {
          "OPENAI_API_KEY": null,
          "tokens": { "access_token": "sentetik-token", "account_id": "acc_sentetik" }
        }
        """);

        try
        {
            var credentials = CodexCredentialStore.TryRead(path);

            Assert.NotNull(credentials);
            Assert.Equal("acc_sentetik", credentials!.AccountId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
