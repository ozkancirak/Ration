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

    public static string Describe(Exception ex) =>
        IsOffline(ex)
            ? L.T("No internet connection", "İnternet bağlantısı yok")
            : L.T($"Network error: {ex.GetType().Name}", $"Ağ hatası: {ex.GetType().Name}");
}
