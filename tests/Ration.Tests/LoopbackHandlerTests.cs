using System.Net;
using System.Net.Sockets;
using System.Text;
using Ration.Core.Providers.Antigravity;

namespace Ration.Tests;

/// <summary>Antigravity yerel istemcisi (sertifika doğrulaması kapalı) yönlendirme izlemez.</summary>
public sealed class LoopbackHandlerTests
{
    [Fact]
    public void Ayarlar_ProxySizVeYonlendirmesiz()
    {
        using var handler = AntigravityLoopbackUsageSource.CreateLoopbackHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task Yonlendirme_Izlenmez_IkinciAdreseIstekGitmez()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        using var redirector = new TcpListener(IPAddress.Loopback, 0);
        redirector.Start();

        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var redirectorPort = ((IPEndPoint)redirector.LocalEndpoint).Port;

        var serve = Task.Run(async () =>
        {
            using var client = await redirector.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.ReadAsync(new byte[2048]);
            var response = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/gizli\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        });

        using var handler = AntigravityLoopbackUsageSource.CreateLoopbackHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Sentetik-Baslik", "gizli-deger");

        using var response = await http.GetAsync($"http://127.0.0.1:{redirectorPort}/");
        await serve;

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.False(target.Pending(), "yönlendirilen adrese bağlantı açıldı");
    }
}
