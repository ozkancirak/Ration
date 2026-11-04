using System.Net;
using System.Text;
using System.Text.Json;
using Ration.Core.Model;
using Ration.Core.Providers.Codex;
using Ration.Core.Usage;

namespace Ration.Tests;

/// <summary>The credit lists here are SYNTHETIC; the ids are made up and must never come back out.</summary>
public sealed class CodexResetCreditsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private const string UsageWithCredits = """
    {
      "plan_type": "plus",
      "rate_limit": { "primary_window": { "used_percent": 10, "limit_window_seconds": 18000, "reset_after_seconds": 600 } },
      "rate_limit_reset_credits": { "available_count": 2, "credits": null }
    }
    """;

    private const string CreditList = """
    {
      "available_count": 2,
      "credits": [
        { "id": "SECRET-ID-LATE", "status": "available", "title": "Full reset (Weekly + 5 hr)", "expires_at": "2026-10-20T00:00:00Z" },
        { "id": "SECRET-ID-USED", "status": "redeemed", "title": "Full reset", "expires_at": "2026-10-02T00:00:00Z" },
        { "id": "SECRET-ID-SOON", "status": "available", "title": "Full reset", "expires_at": "2026-10-03T00:00:00Z" }
      ]
    }
    """;

    private static int Count(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CodexResetCredits.ReadCount(document.RootElement);
    }

    [Fact]
    public void UsageResponse_CarriesTheAvailableCount()
    {
        Assert.Equal(2, CodexUsageParser.Parse(UsageWithCredits).ResetCreditCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "rate_limit_reset_credits": null }""")]
    [InlineData("""{ "rate_limit_reset_credits": { "available_count": "3" } }""")]
    [InlineData("""{ "rate_limit_reset_credits": { "available_count": -4 } }""")]
    [InlineData("""{ "rate_limit_reset_credits": { "available_count": 1.5 } }""")]
    public void MissingOrOddCounts_MeanNone(string json)
    {
        Assert.Equal(0, Count(json));
    }

    [Fact]
    public void NextExpiry_IsTheEarliestOfTheAvailableCredits()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), CodexResetCredits.NextExpiry(CreditList));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "credits": null }""")]
    [InlineData("""{ "credits": [ { "status": "redeemed", "expires_at": "2026-10-03T00:00:00Z" } ] }""")]
    [InlineData("""{ "credits": [ { "status": "available" }, 7, null ] }""")]
    public void NoAvailableCreditWithAnExpiry_MeansNoDeadline(string json)
    {
        Assert.Null(CodexResetCredits.NextExpiry(json));
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

    private static CodexOAuthUsageSource Source(Handler handler) =>
        new(new HttpClient(handler), () => new CodexCredentials("test-token", "acct"));

    private static HttpResponseMessage Route(HttpRequestMessage request, string usage, Func<HttpResponseMessage> list) =>
        request.RequestUri!.AbsolutePath.EndsWith("/wham/usage", StringComparison.Ordinal) ? Json(usage) : list();

    [Fact]
    public async Task Source_ReadsTheCountAndTheNextExpiry_AndNeverTheIds()
    {
        var handler = new Handler(request => Route(request, UsageWithCredits, () => Json(CreditList)));

        var snapshot = await Source(handler).FetchAsync();

        Assert.Equal(new ResetCredits(2, new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero)), snapshot.ResetCredits);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(CodexResetCredits.DetailEndpoint, handler.Requests[1].RequestUri!.ToString());
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(handler.Requests, request => Assert.Equal("test-token", request.Headers.Authorization!.Parameter));
        Assert.DoesNotContain("SECRET-ID", snapshot.ToString() + snapshot.ResetCredits);
    }

    [Fact]
    public async Task Source_AsksForTheListOnlyWhileCreditsAreBanked()
    {
        var handler = new Handler(_ => Json("""{ "plan_type": "plus", "rate_limit_reset_credits": { "available_count": 0 } }"""));

        var snapshot = await Source(handler).FetchAsync();

        Assert.Null(snapshot.ResetCredits);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Source_KeepsTheCount_WhenTheListFails(HttpStatusCode status)
    {
        var handler = new Handler(request => Route(request, UsageWithCredits, () => Json("{}", status)));

        var snapshot = await Source(handler).FetchAsync();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(new ResetCredits(2, null), snapshot.ResetCredits);
    }

    [Fact]
    public async Task Source_KeepsTheCount_WhenTheListIsGarbageOrHuge()
    {
        var garbage = await Source(new Handler(request => Route(request, UsageWithCredits, () => Json("<html>")))).FetchAsync();
        var huge = await Source(new Handler(request => Route(request, UsageWithCredits, () => Json(new string(' ', 200_000) + CreditList)))).FetchAsync();

        Assert.Equal(new ResetCredits(2, null), garbage.ResetCredits);
        Assert.Equal(new ResetCredits(2, null), huge.ResetCredits);
    }

    [Fact]
    public async Task Source_KeepsTheCount_WhenTheListRequestThrows()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/wham/usage", StringComparison.Ordinal)
            ? Json(UsageWithCredits)
            : throw new HttpRequestException("offline"));

        var snapshot = await Source(handler).FetchAsync();

        Assert.Equal(new ResetCredits(2, null), snapshot.ResetCredits);
    }

    private static UsageSnapshot Snapshot(ResetCredits? credits, ProviderStatus status = ProviderStatus.Ok) =>
        new("codex", [], null, null, status, SourceKind.LocalFile, Now, null, ResetCredits: credits);

    private static readonly IReadOnlySet<string> NothingFired = new HashSet<string>();

    [Fact]
    public void Alert_FiresOnceTheNextExpiryIsWithin48Hours()
    {
        var credits = new ResetCredits(2, Now.AddHours(47));

        var alert = Assert.Single(ResetCreditAlerts.Evaluate(Snapshot(credits), "Codex", NothingFired, Now));

        Assert.Contains("Codex", alert.Title);
        Assert.Contains("2", alert.Body);
        Assert.Equal($"codex|resetcredit|{Now.AddHours(47).ToUnixTimeSeconds()}", alert.Key);
    }

    [Fact]
    public void Alert_StaysQuiet_BeforeTheLeadAfterExpiryOnceFiredAndForOldData()
    {
        var soon = new ResetCredits(1, Now.AddHours(10));

        Assert.Empty(ResetCreditAlerts.Evaluate(Snapshot(new ResetCredits(1, Now.AddHours(49))), "Codex", NothingFired, Now));
        Assert.Empty(ResetCreditAlerts.Evaluate(Snapshot(new ResetCredits(1, Now.AddMinutes(-1))), "Codex", NothingFired, Now));
        Assert.Empty(ResetCreditAlerts.Evaluate(Snapshot(new ResetCredits(1, null)), "Codex", NothingFired, Now));
        Assert.Empty(ResetCreditAlerts.Evaluate(Snapshot(new ResetCredits(0, Now.AddHours(10))), "Codex", NothingFired, Now));
        Assert.Empty(ResetCreditAlerts.Evaluate(Snapshot(null), "Codex", NothingFired, Now));
        Assert.Empty(ResetCreditAlerts.Evaluate(Snapshot(soon, ProviderStatus.Degraded), "Codex", NothingFired, Now));

        var key = ResetCreditAlerts.Evaluate(Snapshot(soon), "Codex", NothingFired, Now).Single().Key;
        Assert.Empty(ResetCreditAlerts.Evaluate(Snapshot(soon), "Codex", new HashSet<string> { key }, Now));
    }

    [Fact]
    public void Alert_ASecondDeadlineGetsItsOwnNotification()
    {
        var first = ResetCreditAlerts.Evaluate(Snapshot(new ResetCredits(2, Now.AddHours(5))), "Codex", NothingFired, Now).Single();

        var later = ResetCreditAlerts.Evaluate(
            Snapshot(new ResetCredits(1, Now.AddHours(40))), "Codex", new HashSet<string> { first.Key }, Now);

        Assert.NotEqual(first.Key, Assert.Single(later).Key);
    }

    [Fact]
    public void Describe_NamesTheCount_AndTheTimeLeftWhenKnown()
    {
        var withExpiry = ResetCreditAlerts.Describe(new ResetCredits(3, Now.AddDays(5).AddHours(3)), Now);
        var withoutExpiry = ResetCreditAlerts.Describe(new ResetCredits(1, null), Now);

        Assert.Contains("3", withExpiry);
        Assert.Contains("5", withExpiry);
        Assert.Contains("1", withoutExpiry);
        Assert.True(withExpiry.Length > withoutExpiry.Length);
    }
}
