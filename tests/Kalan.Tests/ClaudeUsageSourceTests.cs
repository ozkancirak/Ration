using System.Net;
using System.Text;
using Kalan.Core.Model;
using Kalan.Core.Providers.Claude;

namespace Kalan.Tests;

public sealed class ClaudeUsageSourceTests
{
    [Fact]
    public async Task SuresiGecmisToken_Yenilenir_VeYeniTokenKotaIcinKullanilir()
    {
        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/v1/oauth/token", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK,
                    """{"access_token":"sentetik-yeni-token","refresh_token":"sentetik-yeni-refresh","expires_in":3600}""")
                : JsonResponse(HttpStatusCode.OK,
                    """{"five_hour":{"utilization":12}}"""));

        ClaudeCredentials? saved = null;
        var source = new ClaudeOAuthUsageSource(
            new HttpClient(handler),
            () => new ClaudeCredentials(
                "sentetik-eski-token",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                "max",
                "sentetik-refresh"),
            credentials =>
            {
                saved = credentials;
                return true;
            });

        var snapshot = await source.FetchAsync();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, handler.Calls.Count);
        Assert.EndsWith("/v1/oauth/token", handler.Calls[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(ClaudeOAuthUsageSource.UsageEndpoint, handler.Calls[1].Uri.ToString());
        Assert.Contains("grant_type", handler.Calls[0].Body, StringComparison.Ordinal);
        Assert.Equal("Bearer sentetik-yeni-token", handler.Calls[1].Authorization);
        Assert.NotNull(saved);
        Assert.Equal("sentetik-yeni-refresh", saved!.RefreshToken);
        Assert.True(source.LastRefreshAttempted);
        Assert.Equal(200, source.LastRefreshStatusCode);
        Assert.True(source.LastRefreshCacheWritten);
        Assert.True(source.LastCredentialsExpired);
    }

    [Fact]
    public async Task Kota429_RetryAfterVeHttpKoduTanidaGorunur()
    {
        var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"rate_limited\"}", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "17");
            return response;
        });

        var source = new ClaudeOAuthUsageSource(
            new HttpClient(handler),
            () => new ClaudeCredentials("sentetik-token", DateTimeOffset.UtcNow.AddHours(1), "max"),
            _ => true);

        var snapshot = await source.FetchAsync();

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(429, source.LastStatusCode);
        Assert.Equal("17", source.LastRetryAfter);
        Assert.False(source.LastRefreshAttempted);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                body));
            return responder(request, body);
        }
    }

    private sealed record Call(Uri Uri, string? Authorization, string Body);
}
