namespace Ration.Core.Providers.Status;

/// <summary>
/// Fetches a provider's public status page. Plain GET, no credentials: HTTPS only, a size limit, and any
/// failure (network, HTTP error, oversized or unreadable body) is just "no report", never an error.
/// </summary>
public static class StatusPageClient
{
    private const int MaxBytes = 1_000_000;

    public static async Task<StatusReport?> FetchAsync(HttpClient http, StatusPageSource source, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(source.SummaryUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            using var response = await TransientRetry
                .SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, uri), ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            return StatusPageParser.Parse(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), source.Components);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
