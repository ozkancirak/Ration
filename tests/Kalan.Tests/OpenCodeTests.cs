using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Kalan.Core.Model;
using Kalan.Core.Providers.OpenCode;

namespace Kalan.Tests;

public sealed class OpenCodeTests
{
    [Fact]
    public void AuthContent_OpencodeGoAnahtariniOkur()
    {
        var credentials = OpenCodeCredentialStore.TryRead(
            contentOverride: """{"opencode-go":{"key":"sentetik-key"}}""");

        Assert.NotNull(credentials);
        Assert.Equal("sentetik-key", credentials!.AccessToken);
    }

    [Fact]
    public void Parser_DolarLimitYuzdeleriniDogruPencerelereKoyar()
    {
        const string json = """
        {
          "usage": {
            "rolling": { "status": "ok", "percent": 12.5, "resetsAt": "2026-09-21T22:00:00Z" },
            "weekly": { "status": "ok", "percent": 40, "resetsAt": "2026-09-28T00:00:00Z" },
            "monthly": { "status": "ok", "percent": 55, "resetsAt": "2026-10-21T00:00:00Z" }
          }
        }
        """;

        var windows = OpenCodeUsageParser.ParseWindows(json);

        Assert.Equal(3, windows.Count);
        Assert.Equal(12.5, Assert.Single(windows, w => w.Kind == WindowKind.Session).Percent);
        Assert.Equal(40, Assert.Single(windows, w => w.Kind == WindowKind.Weekly).Percent);
        Assert.Equal(55, Assert.Single(windows, w => w.Kind == WindowKind.Monthly).Percent);
        Assert.All(windows, w => Assert.Contains("$ limiti", w.Label));
    }

    [Fact]
    public async Task RemoteUsage_BearerHeaderiGonderir()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(OpenCodeUsageSource.UsageEndpoint, request.RequestUri!.ToString());
            Assert.Equal("Bearer sentetik-key", request.Headers.Authorization?.ToString());
            return JsonResponse(HttpStatusCode.OK,
                """{"rolling":{"percent":25,"resetsAt":"2026-09-21T22:00:00Z"}}""");
        });

        var source = new OpenCodeUsageSource(
            new HttpClient(handler),
            () => new OpenCodeCredentials("sentetik-key"),
            databasePath: Path.Combine(Path.GetTempPath(), "kalan-opencode-yok.db"));

        var snapshot = await source.FetchAsync();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(25, Assert.Single(snapshot.Windows).Percent);
        Assert.Equal(200, source.LastStatusCode);
    }

    [Fact]
    public void LocalReader_CanliDbYerineKopyayiSorgular()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kalan-opencode-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "opencode.db");
        var cacheDirectory = Path.Combine(root, "cache");
        Directory.CreateDirectory(root);

        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE session (
                        time_created INTEGER NOT NULL,
                        cost REAL,
                        tokens_input INTEGER,
                        tokens_output INTEGER,
                        tokens_reasoning INTEGER,
                        tokens_cache_read INTEGER,
                        tokens_cache_write INTEGER
                    );
                    INSERT INTO session VALUES ($time, 1.25, 100, 200, 30, 40, 50);
                    """;
                create.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds());
                create.ExecuteNonQuery();
            }

            var report = OpenCodeLocalUsageReader.Read(databasePath, cacheDirectory);

            Assert.NotNull(report);
            Assert.Equal(1.25m, report!.TotalCost);
            Assert.Equal(100, report.InputTokens);
            Assert.Equal(200, report.OutputTokens);
            Assert.Equal(30, report.ReasoningTokens);
            Assert.Equal(40, report.CacheReadTokens);
            Assert.Equal(50, report.CacheCreationTokens);
            Assert.False(Directory.Exists(cacheDirectory) &&
                         Directory.EnumerateDirectories(cacheDirectory).Any());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
