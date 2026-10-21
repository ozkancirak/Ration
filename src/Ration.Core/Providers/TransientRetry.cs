using System.Net;

namespace Ration.Core.Providers;

/// <summary>
/// Geçici ağ hatalarında tek yeniden deneme (CodexBar'ın "transient idempotent" politikası).
///
/// Yalnızca GET gibi tekrarı zararsız istekler için kullanılır. Yeniden denenenler: bağlantı
/// kopması, boş/bozuk yanıt ve 408/500/502/503/504 (sunucu <c>Retry-After</c> verirse en çok
/// 10 sn beklenir). Yeniden DENENMEYENLER: çevrimdışı/DNS hatası ve sertifika hatası (tekrar
/// denemek fayda etmez), 429 (hız sınırı, çağıranın kendi kuralı var), zaman aşımı (zaten uzun
/// bekledi; ikinci bir bekleme turu uzatır) ve iptal.
/// </summary>
public static class TransientRetry
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1);

    /// <param name="createRequest">
    /// Her deneme için yeni istek üretir (<see cref="HttpRequestMessage"/> yeniden gönderilemez).
    /// </param>
    /// <param name="baseDelay">İlk bekleme; testler için ayarlanabilir.</param>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> createRequest,
        CancellationToken ct = default,
        TimeSpan? baseDelay = null)
    {
        var delay = baseDelay ?? DefaultDelay;

        try
        {
            using var request = createRequest();
            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!IsRetryableStatus(response.StatusCode)) return response;

            delay = RetryAfter(response, delay);
            response.Dispose();
        }
        catch (HttpRequestException ex) when (IsTransient(ex))
        {
            // Aşağıdaki ikinci deneme.
        }

        await Task.Delay(delay, ct).ConfigureAwait(false);

        using var second = createRequest();
        return await http.SendAsync(second, ct).ConfigureAwait(false);
    }

    internal static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    internal static bool IsTransient(HttpRequestException ex) =>
        !NetworkErrors.IsOffline(ex) &&
        ex.HttpRequestError is HttpRequestError.ConnectionError
            or HttpRequestError.ResponseEnded
            or HttpRequestError.Unknown;

    private static TimeSpan RetryAfter(HttpResponseMessage response, TimeSpan fallback)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? asked = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

        return asked is { } value
            ? TimeSpan.FromTicks(Math.Clamp(value.Ticks, 0, MaxDelay.Ticks))
            : fallback;
    }
}
