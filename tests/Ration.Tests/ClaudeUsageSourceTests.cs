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
                "sentetik-refresh"),
            Path.Combine(Path.GetTempPath(), $"ration-retry-{Guid.NewGuid():N}.txt"));

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

        var retryFile = Path.Combine(Path.GetTempPath(), $"ration-retry-{Guid.NewGuid():N}.txt");
        try
        {
            var source = new ClaudeOAuthUsageSource(
                new HttpClient(handler),
                () => new ClaudeCredentials("sentetik-token", DateTimeOffset.UtcNow.AddHours(1), "max"),
                retryFile);

            var snapshot = await source.FetchAsync();

            Assert.Equal(ProviderStatus.Error, snapshot.Status);
            Assert.Equal(429, source.LastStatusCode);
            Assert.Equal("17", source.LastRetryAfter);
            Assert.StartsWith("Claude hız sınırına takıldı", snapshot.StaleReason);

            // Süre dolmadan yeni örnek (yeniden başlatma) ağa çıkmaz; diskteki süreye uyar.
            var restarted = new ClaudeOAuthUsageSource(
                new HttpClient(handler),
                () => new ClaudeCredentials("sentetik-token", DateTimeOffset.UtcNow.AddHours(1), "max"),
                retryFile);
            var second = await restarted.FetchAsync();

            Assert.Single(handler.Calls);
            Assert.Equal(ProviderStatus.Error, second.Status);
        }
        finally
        {
            File.Delete(retryFile);
        }
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

public sealed class ClaudeStatusLineTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ration-statusline-{Guid.NewGuid():N}.json");

    // Sentetik: gerçek statusLine girdisinin yalnızca şekli; oturum içeriği yok.
    private const string Input = """
    {"session_id":"sentetik","cwd":"C:/gizli","rate_limits":{"five_hour":{"used_percentage":12.5,"resets_at":1790157600},"seven_day":{"used_percentage":40,"resets_at":1790500000.5}}}
    """;

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Yakalama_YalnizcaKotaAlanlariniYazar_VeGeriOkunur()
    {
        var limits = ClaudeStatusLine.Capture(Input, _path);

        Assert.NotNull(limits);
        Assert.Equal(12.5, limits!.FiveHour);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790500000), limits.SevenDayResetsAt);

        var saved = File.ReadAllText(_path);
        Assert.DoesNotContain("session_id", saved);
        Assert.DoesNotContain("gizli", saved);

        var read = ClaudeStatusLine.Read(_path);
        Assert.Equal(40, read!.Value.Limits.SevenDay);
    }

    [Fact]
    public void EksikGirdi_OncekiTamKaydiEzmez()
    {
        ClaudeStatusLine.Capture(Input, _path);

        Assert.Null(ClaudeStatusLine.Capture("""{"rate_limits":{"five_hour":{"used_percentage":1}}}""", _path));
        Assert.Equal(12.5, ClaudeStatusLine.Read(_path)!.Value.Limits.FiveHour);
    }

    [Fact]
    public async Task TazeKayitOk_EskiKayitVeriyleBirlikteDegraded()
    {
        ClaudeStatusLine.Capture(Input, _path, DateTimeOffset.UtcNow);
        var fresh = await new ClaudeStatusLineUsageSource(_path).FetchAsync();
        Assert.Equal(ProviderStatus.Ok, fresh.Status);
        Assert.Equal(2, fresh.Windows.Count);

        ClaudeStatusLine.Capture(Input, _path, DateTimeOffset.UtcNow.AddHours(-1));
        var stale = await new ClaudeStatusLineUsageSource(_path).FetchAsync();
        Assert.Equal(ProviderStatus.Degraded, stale.Status);
        Assert.Equal(2, stale.Windows.Count);
    }

    [Fact]
    public async Task Resolver_VeriIcerenBayatSonucuHatayaTercihEder()
    {
        ClaudeStatusLine.Capture(Input, _path, DateTimeOffset.UtcNow.AddHours(-1));
        var provider = new StubProvider(new FailingSource(), new ClaudeStatusLineUsageSource(_path));

        var result = await Ration.Core.Providers.ProviderResolver.ResolveAsync(provider);

        Assert.Equal(ProviderStatus.Degraded, result.Status);
        Assert.Equal(2, result.Windows.Count);
    }

    private sealed class FailingSource : Ration.Core.Abstractions.IUsageSource
    {
        public SourceKind Kind => SourceKind.LocalFile;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<UsageSnapshot> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult(Snapshot.Empty("claude", ProviderStatus.AuthRequired, "sentetik 401", Kind));
    }

    private sealed class StubProvider(params Ration.Core.Abstractions.IUsageSource[] sources) : Ration.Core.Abstractions.IUsageProvider
    {
        public string Id => "claude";
        public string DisplayName => "Claude";
        public Ration.Core.Abstractions.ProviderCapabilities Capabilities { get; } = new(true, true, false, false, false);
        public IReadOnlyList<Ration.Core.Abstractions.IUsageSource> Sources { get; } = sources;
    }
}
