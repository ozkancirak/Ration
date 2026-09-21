using Kalan.Core.Model;
using Kalan.Core.Providers.Claude;
using Kalan.Core.Providers.Codex;

namespace Kalan.Tests;

/// <summary>
/// Buradaki JSON'lar TAMAMEN SENTETİKTİR. Gerçek .credentials.json / auth.json
/// içeriğinden kopyalanmış hiçbir şey repoya giremez (AGENTS.md §2.3).
/// </summary>
public class ProviderParsingTests
{
    [Fact]
    public void ClaudeParser_NesneBicimindekiPencereleriOkur()
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
    public void ClaudeParser_DuzSayiBicimindekiPencereleriOkur()
    {
        const string json = """{ "five_hour": 73, "seven_day": 12 }""";

        var windows = ClaudeUsageParser.ParseWindows(json);

        Assert.Equal(2, windows.Count);
        Assert.Equal(73, windows[0].Percent);
        Assert.Null(windows[0].ResetsAt);
    }

    [Fact]
    public void ClaudeParser_TaninmayanAlaniAtlar_UydurmaVeriUretmez()
    {
        const string json = """{ "bambaska_bir_alan": { "utilization": 99 } }""";

        Assert.Empty(ClaudeUsageParser.ParseWindows(json));
    }

    [Fact]
    public void ClaudeParser_AralikDisiYuzdeyiKirpar()
    {
        const string json = """{ "five_hour": 140, "seven_day": -5 }""";

        var windows = ClaudeUsageParser.ParseWindows(json);

        Assert.Equal(100, windows[0].Percent);
        Assert.Equal(0, windows[1].Percent);
    }

    [Fact]
    public void ClaudeParser_EpochSaniyeVeMilisaniyeyiAyirtEder()
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

    [Fact]
    public void CodexParser_BirincilVeIkincilPencereleriOkur()
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
    public void CodexParser_RateLimitSarmalayicisiYoksaKokteArar()
    {
        const string json = """{ "primary_window": { "used_percent": 10 } }""";

        var window = Assert.Single(CodexUsageParser.ParseWindows(json));

        Assert.Equal(WindowKind.Session, window.Kind);
        Assert.Equal(10, window.Percent);
    }

    [Fact]
    public void KimlikOkuyucular_OlmayanDosyaIcinNullDoner()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"kalan-yok-{Guid.NewGuid():N}.json");

        Assert.Null(ClaudeCredentialStore.TryRead(missing));
        Assert.Null(CodexCredentialStore.TryRead(missing));
    }

    [Fact]
    public void ClaudeKimlik_ClaudeAiOauthYoksaNullDoner()
    {
        // Claude Code 2.1.x'te dosyada yalnizca mcpOAuth bulunabiliyor; bu kullanilabilir degil.
        var path = Path.Combine(Path.GetTempPath(), $"kalan-test-{Guid.NewGuid():N}.json");
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
    public void ClaudeKimlik_GecerliDosyayiOkur_VeSuresiDolmusuIsaretler()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kalan-test-{Guid.NewGuid():N}.json");
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
    public void CodexKimlik_TokensBolumunuOkur()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kalan-test-{Guid.NewGuid():N}.json");

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
