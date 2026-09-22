using Kalan.Core.Model;
using Kalan.Core.Cost;
using Kalan.Core.Providers.Antigravity;

namespace Kalan.Tests;

public sealed class AntigravityTests
{
    [Fact]
    public void PortFinder_UsesNewestHttpLine_AndIgnoresGrpc()
    {
        const string log = """
            listening on random port at 4011 for gRPC
            listening on random port at 4012 for HTTP
            listening on random port at 4013 for HTTP
            """;

        Assert.Equal(4013, AntigravityPortFinder.FindPortInLogText(log));
    }

    [Fact]
    public void Parser_MapsFractionsLabelsAndWindowLengths()
    {
        const string json = """
            {
              "groups": [
                {
                  "displayName": "Claude Sonnet",
                  "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
                  "buckets": [
                    { "window": "5h", "remainingFraction": 0.25, "resetTime": "2026-09-21T12:00:00Z" },
                    { "window": "weekly", "remainingFraction": 0.80, "resetTime": "2026-09-27T12:00:00Z" },
                    { "window": "monthly", "remainingFraction": 0.50 }
                  ]
                }
              ]
            }
            """;

        var windows = AntigravityUsageParser.ParseWindows(json);

        Assert.Equal(2, windows.Count);
        Assert.Equal(WindowKind.Session, windows[0].Kind);
        Assert.Equal(75, windows[0].Percent);
        Assert.Equal(TimeSpan.FromHours(5), windows[0].WindowLength);
        Assert.Equal("5 saatlik", windows[0].Label);
        Assert.Equal("Claude Sonnet", windows[0].GroupName);
        Assert.Equal(
            "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
            windows[0].GroupDescription);
        Assert.Equal(WindowKind.Weekly, windows[1].Kind);
        Assert.Equal(20d, windows[1].Percent, precision: 10);
        Assert.Equal("Haftalık", windows[1].Label);
        Assert.Equal("Claude Sonnet", windows[1].GroupName);
        Assert.Equal(TimeSpan.FromDays(7), windows[1].WindowLength);
    }

    [Fact]
    public void Parser_MalformedOrEmptyShape_ReturnsNoWindows()
    {
        Assert.Empty(AntigravityUsageParser.ParseWindows("{\"groups\":[]}"));
        Assert.Empty(AntigravityUsageParser.ParseWindows("{\"groups\": [{\"displayName\": \"x\"}]}"));
    }

    [Theory]
    [InlineData("{\"userStatus\":{\"userTier\":{\"name\":\"Pro\",\"description\":\"Tier\"}}}", "Pro")]
    [InlineData("{\"userStatus\":{\"userTier\":{\"description\":\"Team\"}}}", "Team")]
    [InlineData("{\"userStatus\":{\"planStatus\":{\"planInfo\":{\"planDisplayName\":\"Business\",\"planName\":\"business\"}}}}", "Business")]
    [InlineData("{\"userStatus\":{\"planStatus\":{\"planInfo\":{\"planName\":\"starter\"}}}}", "starter")]
    public void Parser_ReadsPlanNameInDocumentedOrder(string json, string expected)
    {
        Assert.Equal(expected, AntigravityUsageParser.ParsePlanName(json));
    }

    [Fact]
    public async Task Source_ProbesProcessThenDefaultAndOverrideLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kalan-antigravity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var defaultLog = Path.Combine(root, "default.log");
        var overrideLog = Path.Combine(root, "override.log");
        File.WriteAllText(defaultLog, "listening on random port at 41003 for HTTP");
        File.WriteAllText(overrideLog, "listening on random port at 41004 for HTTP");

        try
        {
            var handler = new RecordingHandler();
            using var http = new HttpClient(handler);
            var source = new AntigravityLoopbackUsageSource(
                http,
                defaultLogPath: defaultLog,
                overrideLogPath: overrideLog,
                findProcessPorts: () => new[] { 41001, 41002 });

            var snapshot = await source.FetchAsync(CancellationToken.None);

            Assert.Equal(ProviderStatus.Degraded, snapshot.Status);
            Assert.Equal(new[] { 41001, 41002, 41003, 41004 }, handler.Ports);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Source_SendsOnlyCsrfHeader()
    {
        var handler = new RecordingHandler
        {
            Response = request => request.RequestUri!.AbsolutePath.EndsWith(
                "GetUserStatus", StringComparison.Ordinal)
                ? JsonResponse(
                    System.Net.HttpStatusCode.OK,
                    """{"userStatus":{"userTier":{"name":"Pro"}}}""")
                : JsonResponse(
                    System.Net.HttpStatusCode.OK,
                    """{"groups":[{"displayName":"Gemini","buckets":[{"window":"5h","remainingFraction":0.5}]}]}"""),
        };

        using var http = new HttpClient(handler);
        var source = new AntigravityLoopbackUsageSource(
            http,
            findProcessEndpoints: () =>
                new[] { new AntigravityProcessEndpoint(41005, "csrf-test") },
            rawResponseSink: _ => { });

        var snapshot = await source.FetchAsync(CancellationToken.None);
        var request = Assert.Single(handler.Requests, request =>
            request.RequestUri!.AbsolutePath.EndsWith("RetrieveUserQuotaSummary", StringComparison.Ordinal));
        var statusRequest = Assert.Single(handler.Requests, request =>
            request.RequestUri!.AbsolutePath.EndsWith("GetUserStatus", StringComparison.Ordinal));

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("csrf-test", request.Headers.GetValues("X-Codeium-Csrf-Token").Single());
        Assert.Equal("1", request.Headers.GetValues("Connect-Protocol-Version").Single());
        Assert.False(request.Headers.Contains("host_bridge_token"));
        Assert.Equal("Pro", snapshot.PlanName);
        Assert.Contains("\"ideName\":\"antigravity\"", handler.RequestBodies.Single(body =>
            body.Contains("ideName", StringComparison.Ordinal)));
        Assert.Equal("csrf-test", statusRequest.Headers.GetValues("X-Codeium-Csrf-Token").Single());
    }

    [Fact]
    public async Task Source_RetriesHttpsWhenHttpBodyIdentifiesTlsPort()
    {
        var handler = new RecordingHandler
        {
            Response = request => request.RequestUri!.Scheme == "http"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("Client sent an HTTP request to an HTTPS server"),
                }
                : JsonResponse(
                    System.Net.HttpStatusCode.OK,
                    """{"groups":[{"displayName":"Gemini","buckets":[{"window":"weekly","remainingFraction":0.5}]}]}"""),
        };

        using var http = new HttpClient(handler);
        var source = new AntigravityLoopbackUsageSource(
            http,
            findProcessEndpoints: () =>
                new[] { new AntigravityProcessEndpoint(41006, "csrf-test") },
            rawResponseSink: _ => { });

        var snapshot = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(new[] { "http", "https", "https", "https" }, handler.Requests
            .Select(request => request.RequestUri!.Scheme)
            .ToArray());
    }

    [Fact]
    public async Task Source_ReadsTrajectoryTokensAndDeduplicatesResponses()
    {
        var handler = new RecordingHandler
        {
            Response = request => request.RequestUri!.AbsolutePath switch
            {
                var path when path.EndsWith("RetrieveUserQuotaSummary", StringComparison.Ordinal) =>
                    JsonResponse(
                        System.Net.HttpStatusCode.OK,
                        "{\"groups\":[{\"displayName\":\"Gemini\",\"buckets\":[{\"window\":\"5h\",\"remainingFraction\":0.5}]}]}"),
                var path when path.EndsWith("GetAllCascadeTrajectories", StringComparison.Ordinal) =>
                    JsonResponse(
                        System.Net.HttpStatusCode.OK,
                        "{\"trajectorySummaries\":{\"cascade-a\":{},\"cascade-b\":{}}}"),
                var path when path.EndsWith("GetCascadeTrajectoryGeneratorMetadata", StringComparison.Ordinal) =>
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("cascade-a", StringComparison.Ordinal)
                        ? JsonResponse(
                            System.Net.HttpStatusCode.OK,
                            """
                            {"generatorMetadata":[
                              {"chatModel":{"responseModel":"gemini-3-flash-a","usage":{"responseId":"r1","inputTokens":100,"outputTokens":50,"thinkingOutputTokens":20,"cacheReadTokens":10,"cacheWriteTokens":5}}},
                              {"chatModel":{"responseModel":"gemini-3-flash-a","usage":{"responseId":"r1","inputTokens":100,"outputTokens":50,"thinkingOutputTokens":20,"cacheReadTokens":10,"cacheWriteTokens":5}}}
                            ]}
                            """)
                        : JsonResponse(
                            System.Net.HttpStatusCode.OK,
                            """
                            {"generatorMetadata":[
                              {"chatModel":{"responseModel":"MODEL_PLACEHOLDER_M132","usage":{"messageId":"m2","inputTokens":7,"outputTokens":3,"thinkingOutputTokens":4,"cacheReadTokens":0,"cacheWriteTokens":0}}}
                            ]}
                            """),
                _ => JsonResponse(
                    System.Net.HttpStatusCode.OK,
                    "{\"userStatus\":{\"userTier\":{\"name\":\"Pro\"}}}"),
            },
        };

        using var http = new HttpClient(handler);
        var source = new AntigravityLoopbackUsageSource(
            http,
            findProcessEndpoints: () =>
                new[] { new AntigravityProcessEndpoint(41007, "csrf-test") },
            rawResponseSink: _ => { });

        var snapshot = await source.FetchAsync(CancellationToken.None);

        Assert.NotNull(snapshot.Cost);
        Assert.Equal(107, snapshot.Cost!.InputTokens);
        Assert.Equal(53, snapshot.Cost.OutputTokens);
        Assert.Equal(23, snapshot.Cost.ReasoningTokens);
        Assert.Equal(10, snapshot.Cost.CacheReadTokens);
        Assert.Equal(5, snapshot.Cost.CacheCreationTokens);
        Assert.Equal(2, snapshot.Cost.Models!.Count);
        Assert.Contains(handler.RequestBodies, body => body.Contains("generatorMetadataOffset", StringComparison.Ordinal));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("1", request.Headers.GetValues("Connect-Protocol-Version").Single());
            Assert.Equal("csrf-test", request.Headers.GetValues("X-Codeium-Csrf-Token").Single());
            Assert.False(request.Headers.Contains("host_bridge_token"));
        });
    }

    [Fact]
    public void Parser_DefaultsMissingOptionalTokenFields_AndFallsBackToOutputParts()
    {
        const string json = """
            {
              "generatorMetadata": [
                {
                  "chatModel": {
                    "responseModel": "gemini-test",
                    "usage": {
                      "inputTokens": 100,
                      "thinkingOutputTokens": 20,
                      "responseOutputTokens": 30,
                      "responseId": "r1"
                    }
                  }
                },
                {
                  "chatModel": {
                    "responseModel": "gemini-test",
                    "usage": {
                      "inputTokens": 7,
                      "outputTokens": 3
                    }
                  }
                }
              ]
            }
            """;

        var tally = new TokenTally();
        var accepted = AntigravityTokenParser.AddGeneratorMetadataToTally(
            json,
            tally,
            new HashSet<string>(StringComparer.Ordinal),
            out var skipped);

        Assert.Equal(2, accepted);
        Assert.Equal(0, skipped);
        var model = Assert.Single(tally.Models);
        Assert.Equal(107, model.InputTokens);
        Assert.Equal(53, model.OutputTokens);
        Assert.Equal(20, model.ReasoningTokens);
        Assert.Equal(0, model.CacheReadTokens);
        Assert.Equal(0, model.CacheCreationTokens);
    }

    [Fact]
    public void Parser_SkipsOnlyRecordsMissingInputOrResponseModel()
    {
        const string json = """
            {
              "generatorMetadata": [
                { "chatModel": { "responseModel": "model", "usage": { "outputTokens": 1 } } },
                { "chatModel": { "usage": { "inputTokens": 1, "outputTokens": 1 } } },
                { "chatModel": { "responseModel": "model", "usage": { "inputTokens": 2, "outputTokens": 3 } } }
              ]
            }
            """;

        var tally = new TokenTally();
        var accepted = AntigravityTokenParser.AddGeneratorMetadataToTally(
            json,
            tally,
            new HashSet<string>(StringComparer.Ordinal),
            out var skipped,
            out var missingRequired);

        Assert.Equal(1, accepted);
        Assert.Equal(2, skipped);
        Assert.Equal(2, missingRequired);
    }

    private static HttpResponseMessage JsonResponse(
        System.Net.HttpStatusCode statusCode,
        string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<int> Ports { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Response { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Ports.Add(request.RequestUri!.Port);
            Requests.Add(request);
            RequestBodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
            return Task.FromResult(Response?.Invoke(request) ?? new HttpResponseMessage(
                System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}
