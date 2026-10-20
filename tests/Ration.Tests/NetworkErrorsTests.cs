using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Ration.Core.Providers;

namespace Ration.Tests;

public class NetworkErrorsTests
{
    [Fact]
    public void DnsFailure_IsOffline()
    {
        var ex = new HttpRequestException(
            HttpRequestError.NameResolutionError, "dns", new SocketException((int)SocketError.HostNotFound));

        Assert.True(NetworkErrors.IsOffline(ex));
    }

    [Fact]
    public void NetworkUnreachable_InInnerException_IsOffline()
    {
        var ex = new HttpRequestException(
            "failed", new IOException("io", new SocketException((int)SocketError.NetworkUnreachable)));

        Assert.True(NetworkErrors.IsOffline(ex));
    }

    [Fact]
    public void ConnectionRefused_IsNotOffline()
    {
        var ex = new HttpRequestException(
            "failed", new SocketException((int)SocketError.ConnectionRefused));

        Assert.False(NetworkErrors.IsOffline(ex));
    }

    [Fact]
    public void Timeout_IsNotOffline()
    {
        Assert.False(NetworkErrors.IsOffline(new TaskCanceledException()));
    }

    [Fact]
    public void HttpClientTimeout_IsTimeout_ButPlainCancellationIsNot()
    {
        Assert.True(NetworkErrors.IsTimeout(new TaskCanceledException("t", new TimeoutException())));
        Assert.False(NetworkErrors.IsTimeout(new TaskCanceledException()));
    }

    [Fact]
    public async Task RealHttpClientTimeout_IsRecognizedAsTimeout()
    {
        // Yanıt vermeyen yerel bir sunucu: gerçek HttpClient zaman aşımının istisna zincirini doğrular.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => http.GetAsync($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"));

        Assert.True(NetworkErrors.IsTimeout(ex));
        Assert.False(NetworkErrors.IsOffline(ex));
    }

    [Fact]
    public void Describe_Timeout_DoesNotShowTheExceptionName()
    {
        var text = NetworkErrors.Describe(new TaskCanceledException("t", new TimeoutException()));

        Assert.Equal("Sunucu zamanında yanıt vermedi", text);
        Assert.DoesNotContain("TaskCanceledException", text);
    }
}
