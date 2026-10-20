using System.Net.Http;
using System.Net.Sockets;

namespace Ration.Core.Providers;

/// <summary>
/// Ağ hatasını kullanıcıya anlatır. "Çevrimdışı" ile "sunucu sorunu" ayrılır ki bağlantı
/// yokken kota hatası gibi görünmesin.
/// </summary>
public static class NetworkErrors
{
    public static bool IsOffline(Exception ex)
    {
        if (ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
        {
            return true;
        }

        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException
                {
                    SocketErrorCode: SocketError.HostNotFound
                        or SocketError.TryAgain
                        or SocketError.NetworkUnreachable
                        or SocketError.NetworkDown
                        or SocketError.HostUnreachable
                })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// HttpClient zaman aşımı, iç istisnası <see cref="TimeoutException"/> olan bir
    /// <see cref="TaskCanceledException"/> olarak gelir; kullanıcı iptali bunu taşımaz.
    /// </summary>
    public static bool IsTimeout(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException) return true;
        }

        return false;
    }

    public static string Describe(Exception ex) =>
        IsOffline(ex)
            ? L.T("No internet connection", "İnternet bağlantısı yok")
            : IsTimeout(ex)
                ? L.T("The server did not respond in time", "Sunucu zamanında yanıt vermedi")
                : L.T($"Network error: {ex.GetType().Name}", $"Ağ hatası: {ex.GetType().Name}");
}
