using System.Text.Json;
using Ration.Core.Model;

namespace Ration.Core.Providers.Codex;

/// <summary>
/// Banked rate-limit reset credits on a Codex account (read only). The wham/usage response carries
/// <c>rate_limit_reset_credits.available_count</c>; while it is above zero, the credit list lives at a second
/// endpoint and gives each credit a status and an expiry. The count always comes from the usage response and the
/// list only adds the next expiry, so a failed or unexpected list response keeps the count.
/// A credit's <c>id</c> is the handle that redeems it: it is never read, kept or logged here.
/// </summary>
public static class CodexResetCredits
{
    public const string DetailEndpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    private const int MaxBytes = 64 * 1024;

    /// <summary>The count from the usage response; 0 when the field is missing or has another type.</summary>
    public static int ReadCount(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_reset_credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Object ||
            !credits.TryGetProperty("available_count", out var count) ||
            count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt32(out var available))
        {
            return 0;
        }

        return Math.Max(available, 0);
    }

    /// <summary>The earliest expiry among the credits that are still available; null when there is none.</summary>
    public static DateTimeOffset? NextExpiry(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("credits", out var credits) ||
                credits.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            DateTimeOffset? next = null;
            foreach (var credit in credits.EnumerateArray())
            {
                if (credit.ValueKind != JsonValueKind.Object ||
                    !string.Equals(JsonHelpers.ReadString(credit, "status"), "available", StringComparison.OrdinalIgnoreCase) ||
                    !credit.TryGetProperty("expires_at", out var expiry) ||
                    JsonHelpers.ReadTimestamp(expiry) is not { } at)
                {
                    continue;
                }

                if (next is null || at < next) next = at;
            }

            return next;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The credits to report: null without any, otherwise the count plus, when the list can be read, the next expiry.
    /// </summary>
    public static async Task<ResetCredits?> ReadAsync(
        HttpClient http,
        Func<HttpRequestMessage> createRequest,
        int count,
        CancellationToken ct = default)
    {
        if (count <= 0) return null;

        DateTimeOffset? next = null;
        try
        {
            // No retry on purpose: this call only adds detail, and a slow answer must not delay the quota.
            using var request = createRequest();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new MemoryStream();
                var chunk = new byte[8 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxBytes)
                    {
                        buffer = null;
                        break;
                    }

                    buffer.Write(chunk, 0, read);
                }

                if (buffer is not null)
                {
                    next = NextExpiry(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // Keep the count from the usage response.
        }

        return new ResetCredits(count, next);
    }
}
