using System.Net;

namespace Ration.Core.Providers;

/// <summary>
/// A single retry on transient network errors (CodexBar's "transient idempotent" policy).
///
/// Used only for requests whose repetition is harmless, like GET. Retried: connection
/// drops, empty/corrupt responses and 408/500/502/503/504 (if the server sends <c>Retry-After</c>,
/// it waits at most 10 s). NOT retried: offline/DNS errors and certificate errors (retrying does not
/// help), 429 (rate limit, the caller has its own rule), timeouts (it already waited long; a second
/// wait round would stretch things) and cancellation.
/// </summary>
public static class TransientRetry
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1);

    /// <param name="createRequest">
    /// Creates a new request for every attempt (an <see cref="HttpRequestMessage"/> cannot be sent again).
    /// </param>
    /// <param name="baseDelay">The first wait; adjustable for tests.</param>
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
            // The second attempt below.
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
