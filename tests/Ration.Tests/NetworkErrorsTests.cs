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
}
