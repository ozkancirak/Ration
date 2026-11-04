using System.Net;
using System.Text;
using Ration.Core.Providers.Status;

namespace Ration.Tests;

/// <summary>The JSON here has the shape of a Statuspage v2 summary.json; the names and states are synthetic.</summary>
public class StatusPageTests
{
    private static readonly string[] Watched = ["Claude Code", "Claude API (api.anthropic.com)"];

    private static string Page(string indicator, params (string Name, string Status)[] components)
    {
        var items = string.Join(",", components.Select(c => "{\"name\":\"" + c.Name + "\",\"status\":\"" + c.Status + "\"}"));
        return "{\"status\":{\"indicator\":\"" + indicator + "\",\"description\":\"Partially Degraded Service\"},\"components\":[" + items + "]}";
    }

    [Fact]
    public void UnrelatedOutage_DoesNotRaiseABadge_WhenTheWatchedComponentsAreFine()
    {
        var json = Page("minor",
            ("claude.ai", "partial_outage"),
            ("Claude Code", "operational"),
            ("Claude API (api.anthropic.com)", "operational"));

        var report = StatusPageParser.Parse(json, Watched);

        Assert.Equal(new StatusReport(OutageLevel.None, null), report);
    }

    [Fact]
    public void WatchedPartialOutage_IsDegraded_AndNamesTheComponent()
    {
        var report = StatusPageParser.Parse(Page("minor", ("Claude Code", "partial_outage")), Watched);

        Assert.Equal(OutageLevel.Degraded, report!.Level);
        Assert.Equal("Claude Code: kısmi kesinti", report.Detail);
    }

    [Fact]
    public void MajorOutage_WinsOverDegraded()
    {
        var json = Page("major",
            ("Claude Code", "degraded_performance"),
            ("Claude API (api.anthropic.com)", "major_outage"));

        var report = StatusPageParser.Parse(json, Watched);

        Assert.Equal(OutageLevel.Outage, report!.Level);
        Assert.Contains("Claude Code", report.Detail);
        Assert.Contains("Claude API (api.anthropic.com)", report.Detail);
    }

    [Fact]
    public void ScheduledMaintenance_IsNotAnOutage()
    {
        var report = StatusPageParser.Parse(Page("maintenance", ("Claude Code", "under_maintenance")), Watched);

        Assert.Equal(OutageLevel.None, report!.Level);
    }

    [Theory]
    [InlineData("none", OutageLevel.None)]
    [InlineData("minor", OutageLevel.Degraded)]
    [InlineData("major", OutageLevel.Outage)]
    [InlineData("critical", OutageLevel.Outage)]
    public void RenamedComponents_FallBackToTheOverallIndicator(string indicator, OutageLevel expected)
    {
        var report = StatusPageParser.Parse(Page(indicator, ("Something else", "operational")), Watched);

        Assert.Equal(expected, report!.Level);
        Assert.Equal(expected == OutageLevel.None ? null : "Partially Degraded Service", report.Detail);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"components\":[]}")]
    public void UnrecognizedShapes_GiveNoReport(string json)
    {
        Assert.Null(StatusPageParser.Parse(json, Watched));
    }

    [Fact]
    public void LongDetails_AreShortened()
    {
        var name = new string('x', 300);

        var report = StatusPageParser.Parse(Page("minor", (name, "partial_outage")), [name]);

        Assert.Equal(140, report!.Detail!.Length);
        Assert.EndsWith("…", report.Detail);
    }

    [Fact]
    public void OnlyProvidersWithAStatusPage_HaveAFeed()
    {
        foreach (var id in new[] { "claude", "codex", "cursor", "CLAUDE" })
        {
            var source = ProviderStatusPages.For(id);
            Assert.NotNull(source);
            Assert.StartsWith("https://", source.SummaryUrl);
            Assert.NotEmpty(source.Components);
        }

        Assert.Null(ProviderStatusPages.For("antigravity"));
        Assert.Null(ProviderStatusPages.For("opencode"));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Fetch_SendsAPlainGet_AndParsesTheReport()
    {
        var handler = new Handler(_ => Json(Page("minor", ("Claude Code", "major_outage"))));
        var source = new StatusPageSource("https://status.example.com/api/v2/summary.json", ["Claude Code"]);

        var report = await StatusPageClient.FetchAsync(new HttpClient(handler), source);

        Assert.Equal(OutageLevel.Outage, report!.Level);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Fetch_IgnoresNonHttpsAddresses_WithoutAnyRequest()
    {
        var handler = new Handler(_ => Json(Page("none")));
        var source = new StatusPageSource("http://status.example.com/api/v2/summary.json", ["Claude Code"]);

        Assert.Null(await StatusPageClient.FetchAsync(new HttpClient(handler), source));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Fetch_HttpErrorsAndOversizedBodies_GiveNoReport()
    {
        var source = new StatusPageSource("https://status.example.com/api/v2/summary.json", ["Claude Code"]);

        var notFound = new Handler(_ => Json("{}", HttpStatusCode.NotFound));
        Assert.Null(await StatusPageClient.FetchAsync(new HttpClient(notFound), source));

        var huge = new Handler(_ => Json("{\"pad\":\"" + new string('a', 1_200_000) + "\"}"));
        Assert.Null(await StatusPageClient.FetchAsync(new HttpClient(huge), source));
    }
}
