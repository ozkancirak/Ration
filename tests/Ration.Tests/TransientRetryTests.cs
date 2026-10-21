using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Ration.Core.Providers;

namespace Ration.Tests;

public sealed class TransientRetryTests
{
    private static readonly TimeSpan NoWait = TimeSpan.Zero;

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var step = steps[Math.Min(Requests.Count - 1, steps.Length - 1)];
            return Task.FromResult(step(request));
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode code) =>
        _ => new HttpResponseMessage(code);

    private static Func<HttpRequestMessage, HttpResponseMessage> Throw(Exception ex) =>
        _ => throw ex;

    private static Task<HttpResponseMessage> Send(HttpMessageHandler handler, TimeSpan? delay = null) =>
        TransientRetry.SendAsync(
            new HttpClient(handler),
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/usage"),
            baseDelay: delay ?? NoWait);

    [Fact]
    public async Task Gecici5xx_TekSefer_YenidenDener()
    {
        var handler = new SequenceHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK));

        using var response = await Send(handler);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.NotSame(handler.Requests[0], handler.Requests[1]); // her deneme yeni istek
    }

    [Fact]
    public async Task IkiKezHata_SonYaniti_DonerVeUcuncuDenemeYapmaz()
    {
        var handler = new SequenceHandler(Status(HttpStatusCode.BadGateway));

        using var response = await Send(handler);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task BaglantiKopmasi_YenidenDener()
    {
        var handler = new SequenceHandler(
            Throw(new HttpRequestException(HttpRequestError.ConnectionError, "reset")),
            Status(HttpStatusCode.OK));

        using var response = await Send(handler);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task KaliciYanitlarVeHizSiniri_YenidenDenenmez(HttpStatusCode code)
    {
        var handler = new SequenceHandler(Status(code));

        using var response = await Send(handler);

        Assert.Equal(code, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CevrimdisiVeSertifikaHatasi_YenidenDenenmez()
    {
        var offline = new SequenceHandler(Throw(new HttpRequestException(
            HttpRequestError.NameResolutionError, "dns", new SocketException((int)SocketError.HostNotFound))));
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(offline));
        Assert.Single(offline.Requests);

        var tls = new SequenceHandler(Throw(new HttpRequestException(HttpRequestError.SecureConnectionError, "tls")));
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(tls));
        Assert.Single(tls.Requests);
    }

    [Fact]
    public async Task ZamanAsimi_YenidenDenenmez()
    {
        var handler = new SequenceHandler(Throw(new TaskCanceledException("t", new TimeoutException())));

        await Assert.ThrowsAsync<TaskCanceledException>(() => Send(handler));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RetryAfter_BeklemeSuresineUyulur()
    {
        // Taban bekleme 1 saat; sunucu "hemen" dediği için test beklemeden biter.
        var handler = new SequenceHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            },
            Status(HttpStatusCode.OK));

        using var response = await Send(handler, TimeSpan.FromHours(1)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }
}
