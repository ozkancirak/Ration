using System.Net.Http;
using System.Net.Sockets;

namespace Ration.Core.Providers;

/// <summary>
/// Explains a network error to the user. "Offline" is told apart from "server problem" so that
/// it does not look like a quota error when there is no connection.
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
    /// An HttpClient timeout arrives as a <see cref="TaskCanceledException"/> whose inner exception is a
    /// <see cref="TimeoutException"/>; a user cancellation does not carry it.
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
