using Kalan.Core.Model;
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
        Assert.Equal(new[] { "http", "https", "https" }, handler.Requests
            .Select(request => request.RequestUri!.Scheme)
            .ToArray());
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
