using System.Net;
using System.Text;
using Ration.Core.Model;
using Ration.Core.Providers.Claude;

namespace Ration.Tests;

public sealed class ClaudeUsageSourceTests
{
    [Fact]
    public async Task SuresiGecmisToken_YenilemedenGonderilir_Ve401deOturumMesajiDoner()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(ClaudeOAuthUsageSource.UsageEndpoint, request.RequestUri!.ToString());
            return JsonResponse(HttpStatusCode.Unauthorized,
                """{"error":{"type":"authentication_error"}}""");
        });

        var source = new ClaudeOAuthUsageSource(
            new HttpClient(handler),
            () => new ClaudeCredentials(
                "sentetik-eski-token",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                "max",
                "sentetik-refresh"));

        var snapshot = await source.FetchAsync();

        Assert.Equal(ProviderStatus.AuthRequired, snapshot.Status);
        Assert.Single(handler.Calls);
        Assert.Equal("Bearer sentetik-eski-token", handler.Calls[0].Authorization);
        Assert.Equal("Oturum yenilenmeli — Claude Code'u bir kez çalıştır", snapshot.StaleReason);
        Assert.Equal(401, source.LastStatusCode);
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
            () => new ClaudeCredentials("sentetik-token", DateTimeOffset.UtcNow.AddHours(1), "max"));

        var snapshot = await source.FetchAsync();

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(429, source.LastStatusCode);
        Assert.Equal("17", source.LastRetryAfter);
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
