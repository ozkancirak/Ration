using System.Net;
using System.Text;
using Ration.Core.Model;
using Ration.Core.Providers.Cursor;

namespace Ration.Tests;

public sealed class CursorUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private const string ProBody = """
        {
          "membershipType": "pro",
          "billingCycleStart": "2026-10-01T00:00:00.000Z",
          "billingCycleEnd": "2026-11-01T00:00:00.000Z",
          "individualUsage": {
            "plan": { "enabled": true, "used": 400, "limit": 2000, "remaining": 1600 },
            "onDemand": { "enabled": true, "used": 500, "limit": 10000 }
          }
        }
        """;

    private static CursorSession Session(DateTimeOffset? expires = null) =>
        new("sentetik-token", "user_ABC123", expires ?? Now.AddDays(5), "pro", "active");

    private static CursorUsageSource Source(
        HttpMessageHandler handler,
        CursorSession? session,
        bool installed = true,
        bool encryptedToken = false) =>
        new(new HttpClient(handler), () => session, () => installed, () => Now, () => encryptedToken);

    [Fact]
    public void Ayristirici_PlanVeIsteğeBagliPencereleriOranlaOkur()
    {
        var data = CursorUsageParser.Parse(ProBody);

        Assert.Equal("Pro", data.PlanName);
        Assert.Equal(2, data.Windows.Count);

        var plan = data.Windows[0];
        Assert.Equal(WindowKind.Monthly, plan.Kind);
        Assert.Equal(20, plan.Percent);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), plan.ResetsAt);
        Assert.Equal(TimeSpan.FromDays(31), plan.WindowLength);

        Assert.Equal(5, data.Windows[1].Percent);
    }

    [Fact]
    public void Ayristirici_BildirilenYuzdeyiOncelikliKullanir_VeSinirlar()
    {
        var data = CursorUsageParser.Parse(
            """{"individualUsage":{"plan":{"used":1,"limit":10,"totalPercentUsed":137.5}}}""");

        Assert.Equal(100, Assert.Single(data.Windows).Percent);
    }

    [Fact]
    public void Ayristirici_KapaliVeSinirsizOnDemand_PenceREOlmaz_TanimadigiSekilBosDoner()
    {
        var kapali = CursorUsageParser.Parse(
            """{"individualUsage":{"plan":{"used":5,"limit":50},"onDemand":{"enabled":false,"used":0,"limit":100}}}""");
        Assert.Single(kapali.Windows);

        var sinirsiz = CursorUsageParser.Parse(
            """{"individualUsage":{"onDemand":{"enabled":true,"used":3,"limit":null}}}""");
        Assert.Empty(sinirsiz.Windows);

        Assert.Empty(CursorUsageParser.Parse("""{"beklenmeyen":true}""").Windows);
        Assert.Empty(CursorUsageParser.Parse("[1,2,3]").Windows);
    }

    [Fact]
    public void Ayristirici_SinirsizVeyaHaksizPlan_SifirYuzdeUretmez()
    {
        // Canlı yanıttan gözlenen biçim: ücretsiz/iptal edilmiş hesapta limit 0, yüzde 0.
        var data = CursorUsageParser.Parse(
            """
            {"membershipType":"free","billingCycleEnd":"2026-11-10T00:00:00.000Z",
             "individualUsage":{"plan":{"enabled":true,"used":0,"limit":0,"remaining":0,"totalPercentUsed":0},
                                "onDemand":{"enabled":false,"used":0,"limit":null,"remaining":null}}}
            """);

        Assert.Empty(data.Windows);
        Assert.Equal("Free", data.PlanName);
    }

    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("free_trial", "Free trial")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void PlanAdi_Bicimlenir(string? input, string? expected) =>
        Assert.Equal(expected, CursorUsageParser.FormatPlan(input));

    [Fact]
    public async Task Basarili_CerezBasligiSadeceCursorAdresineGider()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, ProBody));

        var snapshot = await Source(handler, Session()).FetchAsync();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Pro", snapshot.PlanName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(CursorUsageSource.UsageEndpoint, request.Uri);
        Assert.Equal("WorkosCursorSessionToken=user_ABC123%3A%3Asentetik-token", request.Cookie);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task KuruluDegilse_AgaCikmadanNotInstalled()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, ProBody));

        var snapshot = await Source(handler, Session(), installed: false).FetchAsync();

        Assert.Equal(ProviderStatus.NotInstalled, snapshot.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OturumYokVeyaSuresiDolmus_AgaCikmadanAuthRequired()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, ProBody));

        var yok = await Source(handler, null).FetchAsync();
        var dolmus = await Source(handler, Session(Now.AddSeconds(20))).FetchAsync();

        Assert.Equal(ProviderStatus.AuthRequired, yok.Status);
        Assert.Equal(ProviderStatus.AuthRequired, dolmus.Status);
        Assert.Contains("açın", dolmus.StaleReason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SifreliBelirtec_GirisYapinDemez_AgaCikmaz()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, ProBody));

        var sifreli = await Source(handler, null, encryptedToken: true).FetchAsync();
        var girisYok = await Source(handler, null).FetchAsync();

        Assert.Equal(ProviderStatus.AuthRequired, sifreli.Status);
        Assert.True(
            sifreli.StaleReason!.Contains("şifreli", StringComparison.OrdinalIgnoreCase) ||
            sifreli.StaleReason.Contains("encrypted", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(girisYok.StaleReason, sifreli.StaleReason);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderStatus.AuthRequired)]
    [InlineData(HttpStatusCode.Forbidden, ProviderStatus.AuthRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderStatus.Error)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderStatus.Error)]
    public async Task HttpHatalari_DurumaEslenir(HttpStatusCode code, ProviderStatus expected)
    {
        var snapshot = await Source(new Handler(_ => Json(code, "{}")), Session()).FetchAsync();

        Assert.Equal(expected, snapshot.Status);
    }

    [Fact]
    public async Task BozukYanitVeBilinmeyenSema_Hata_VeDegraded()
    {
        var bozuk = await Source(new Handler(_ => Json(HttpStatusCode.OK, "<html>")), Session()).FetchAsync();
        var bos = await Source(new Handler(_ => Json(HttpStatusCode.OK, "{}")), Session()).FetchAsync();

        Assert.Equal(ProviderStatus.Error, bozuk.Status);
        Assert.Equal(ProviderStatus.Degraded, bos.Status);
        Assert.Equal("Pro", bos.PlanName); // yanıtta plan yoksa yerel oturumdaki plan kullanılır
    }

    [Fact]
    public async Task ZamanAsimi_AnlasilirMesajVerir()
    {
        var handler = new Handler(_ => throw new TaskCanceledException("t", new TimeoutException()));

        var snapshot = await Source(handler, Session()).FetchAsync();

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("Sunucu zamanında yanıt vermedi", snapshot.StaleReason);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Seen(string Uri, string? Cookie, string? Authorization);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join("; ", cookie) : null,
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(responder(request));
        }
    }
}
