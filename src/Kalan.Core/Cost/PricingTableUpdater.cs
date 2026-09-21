using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kalan.Core.Diagnostics;

namespace Kalan.Core.Cost;

/// <summary>
/// LiteLLM model fiyatlarını haftalık olarak indirip Kalan'ın kendi cache'ine
/// yazar. Ağ kesintisi hiçbir zaman mevcut cache'i silmez veya açılışı bloklamaz.
/// </summary>
public static class PricingTableUpdater
{
    public const string SourceUrl =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static event Action? Updated;

    public static async Task<bool> RefreshIfDueAsync(
        HttpClient? http = null,
        string? path = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        path ??= PricingTable.DefaultPath;
        var current = now ?? DateTimeOffset.UtcNow;

        if (!IsDue(path, current)) return false;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Başka bir pencere aynı anda indirmiş olabilir.
            current = now ?? DateTimeOffset.UtcNow;
            if (!IsDue(path, current)) return false;

            var ownsClient = http is null;
            http ??= CreateClient();
            try
            {
                using var response = await http.GetAsync(SourceUrl, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var downloadedAt = DateTimeOffset.UtcNow;
                var table = PricingTable.FromLiteLlmJson(json, downloadedAt);
                if (table.IsEmpty)
                {
                    Trace.Error("pricing", "refresh failed reason=empty-table");
                    return false;
                }

                SaveAtomically(path, table.ToCacheJson(SourceUrl));
                Trace.Info("pricing", $"refresh status=ok models={table.Rates.Count} source=litellm");
                Updated?.Invoke();
                return true;
            }
            finally
            {
                if (ownsClient) http.Dispose();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Trace.Error("pricing", "refresh failed reason=timeout");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.Error("pricing", $"refresh failed type={ex.GetType().Name}");
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsDue(string path, DateTimeOffset now)
    {
        var downloadedAt = PricingTable.TryReadDownloadedAt(path);
        return downloadedAt is null || now - downloadedAt.Value >= RefreshInterval;
    }

    private static HttpClient CreateClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static void SaveAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("Pricing cache directory is empty.");
        }

        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
