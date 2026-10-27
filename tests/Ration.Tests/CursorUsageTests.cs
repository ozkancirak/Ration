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
    public async Task OzetYolu_CerezBasligiSadeceCursorAdresineGider()
    {
        // Güncel dönem uç noktası 404 verir: özet yoluna düşülür.
        var handler = new Handler(request => request.RequestUri!.AbsoluteUri == CursorUsageSource.CurrentPeriodEndpoint
            ? Json(HttpStatusCode.NotFound, "{}")
            : Json(HttpStatusCode.OK, ProBody));

        var snapshot = await Source(handler, Session()).FetchAsync();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Pro", snapshot.PlanName);
        Assert.Equal(2, handler.Requests.Count);
        var summary = handler.Requests[1];
        Assert.Equal("GET", summary.Method);
        Assert.Equal(CursorUsageSource.UsageEndpoint, summary.Uri);
        Assert.Equal("WorkosCursorSessionToken=user_ABC123%3A%3Asentetik-token", summary.Cookie);
        Assert.Null(summary.Authorization);
    }

    private const string CurrentPeriodBody = """
        {
          "billingCycleStart": "1791664956282",
          "billingCycleEnd": "1794343356282",
          "planUsage": { "remainingBonus": false, "autoPercentUsed": 12, "apiPercentUsed": 30, "totalPercentUsed": 37.5 },
          "spendLimitUsage": { "pooledLimit": "0", "pooledRemaining": "0", "individualLimit": 0, "limitType": "user", "overallLimit": 0, "overallRemaining": 0 },
          "displayMessage": "You've used 37% of your included usage"
        }
        """;

    private static Handler CurrentPeriodHandler(string body = CurrentPeriodBody, HttpStatusCode status = HttpStatusCode.OK) =>
        new(request => request.RequestUri!.AbsoluteUri == CursorUsageSource.CurrentPeriodEndpoint
            ? Json(status, body)
            : Json(HttpStatusCode.OK, ProBody));

    [Fact]
    public async Task GuncelDonem_OnceDener_BelirtecYalnizBearerOlarakCursorSunucusunaGider()
    {
        var handler = CurrentPeriodHandler();

        var snapshot = await Source(handler, Session()).FetchAsync();

        var request = Assert.Single(handler.Requests); // özete gerek kalmadı
        Assert.Equal("POST", request.Method);
        Assert.Equal(CursorUsageSource.CurrentPeriodEndpoint, request.Uri);
        Assert.Equal("Bearer sentetik-token", request.Authorization);
        Assert.Null(request.Cookie);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Pro", snapshot.PlanName);
        var plan = Assert.Single(snapshot.Windows);
        Assert.Equal(37.5, plan.Percent);
        Assert.Equal(WindowKind.Monthly, plan.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794343356282), plan.ResetsAt);
        Assert.Equal(TimeSpan.FromDays(31), plan.WindowLength);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    [InlineData(HttpStatusCode.OK, "{\"beklenmeyen\":true}")]
    [InlineData(HttpStatusCode.OK, "{\"planUsage\":{}}")]
    public async Task GuncelDonemBasarisizsa_EskiOzeteDuser(HttpStatusCode status, string body)
    {
        var handler = CurrentPeriodHandler(body, status);

        var snapshot = await Source(handler, Session()).FetchAsync();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(CursorUsageSource.UsageEndpoint, handler.Requests[1].Uri);
        Assert.Equal(20, snapshot.Windows[0].Percent); // ProBody: 400/2000
    }

    [Fact]
    public async Task GuncelDonemAgHatasi_OzeteDuser()
    {
        var handler = new Handler(request => request.RequestUri!.AbsoluteUri == CursorUsageSource.CurrentPeriodEndpoint
            ? throw new HttpRequestException("ag")
            : Json(HttpStatusCode.OK, ProBody));

        var snapshot = await Source(handler, Session()).FetchAsync();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task IsteGoreHarcamaLimitiVarsa_OzetKullanilir_OnDemandPenceresiKaybolmaz()
    {
        var limitli = CurrentPeriodBody.Replace("\"overallLimit\": 0", "\"overallLimit\": 10000");
        var handler = CurrentPeriodHandler(limitli);

        var snapshot = await Source(handler, Session()).FetchAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, snapshot.Windows.Count); // Plan + On-demand (özetten)
    }

    [Fact]
    public async Task UcretsizHesapta_GuncelDonemSifir_SinirYokDer()
    {
        var handler = CurrentPeriodHandler(CurrentPeriodBody
            .Replace("\"autoPercentUsed\": 12", "\"autoPercentUsed\": 0")
            .Replace("\"apiPercentUsed\": 30", "\"apiPercentUsed\": 0")
            .Replace("\"totalPercentUsed\": 37.5", "\"totalPercentUsed\": 0"));

        var snapshot = await Source(handler, Session() with { MembershipType = "free" }).FetchAsync();

        Assert.Single(handler.Requests);
        Assert.Equal(ProviderStatus.Degraded, snapshot.Status);
        Assert.Empty(snapshot.Windows);
        Assert.Equal("Free", snapshot.PlanName);
    }

    [Fact]
    public void GuncelDonemAyristirici_YuzdeKaynaklari_VeBicimler()
    {
        // Toplam yoksa havuzların en yükseği; sayı olarak verilen epoch da okunur.
        var havuz = CursorCurrentPeriodParser.Parse(
            """{"billingCycleStart":1791664956282,"billingCycleEnd":1794343356282,"planUsage":{"autoPercentUsed":12,"apiPercentUsed":30}}""",
            "pro");
        Assert.Equal(30, Assert.Single(havuz!.Windows).Percent);

        Assert.Equal(100, Assert.Single(CursorCurrentPeriodParser.Parse("""{"planUsage":{"totalPercentUsed":137.5}}""", "pro")!.Windows).Percent);

        Assert.Null(CursorCurrentPeriodParser.Parse("[1,2]", "pro"));
        Assert.Null(CursorCurrentPeriodParser.Parse("""{"planUsage":"yok"}""", "pro"));

        Assert.False(CursorCurrentPeriodParser.HasSpendLimit(CurrentPeriodBody));
        Assert.True(CursorCurrentPeriodParser.HasSpendLimit("""{"spendLimitUsage":{"pooledLimit":"5000"}}"""));
        Assert.False(CursorCurrentPeriodParser.HasSpendLimit("[]"));
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

    private sealed record Seen(string Method, string Uri, string? Cookie, string? Authorization);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join("; ", cookie) : null,
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(responder(request));
        }
    }
}
