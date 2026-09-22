using System.Net.Http;
using System.Text;
using System.Text.Json;
using Ration.Core.Diagnostics;
using Ration.Core.Providers;

namespace Ration.Core.Cost;

/// <summary>
/// LiteLLM ve models.dev fiyatlarını haftalık olarak indirip Ration'ın kendi
/// cache'ine yazar. Ağ kesintisi hiçbir zaman mevcut cache'i silmez veya açılışı
/// bloklamaz; kullanıcı override'ları yalnızca eksik kayıtları tamamlar.
/// </summary>
public static class PricingTableUpdater
{
    public const string SourceUrl =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    public const string ModelsDevSourceUrl = "https://models.dev/api.json";

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
                var downloadedAt = DateTimeOffset.UtcNow;
                PricingTable? table = null;
                var sources = new List<string>();

                var liteLlm = await DownloadAsync(
                    http,
                    SourceUrl,
                    PricingTable.FromLiteLlmJson,
                    "litellm",
                    ct).ConfigureAwait(false);
                if (liteLlm is not null)
                {
                    table = liteLlm;
                    sources.Add("litellm");
                    Trace.Info("pricing", $"source=litellm models={liteLlm.Rates.Count}");
                }

                var modelsDev = await DownloadAsync(
                    http,
                    ModelsDevSourceUrl,
                    PricingTable.FromModelsDevJson,
                    "models.dev",
                    ct).ConfigureAwait(false);
                if (modelsDev is not null)
                {
                    if (table is null)
                    {
                        table = modelsDev;
                        sources.Add("models.dev");
                        Trace.Info("pricing", $"source=models.dev models={modelsDev.Rates.Count}");
                    }
                    else
                    {
                        table = table.MergeMissing(modelsDev, out var added);
                        if (added > 0) sources.Add("models.dev");
                        Trace.Info("pricing", $"source=models.dev added={added}");
                    }
                }

                var overrides = PricingTable.LoadOrEmpty(KnownPaths.PricingOverridesFile);
                if (!overrides.IsEmpty)
                {
                    if (table is null)
                    {
                        table = overrides;
                        sources.Add("override");
                        Trace.Info("pricing", $"source=override models={overrides.Rates.Count}");
                    }
                    else
                    {
                        table = table.MergeMissing(overrides, out var added);
                        if (added > 0) sources.Add("override");
                        Trace.Info("pricing", $"source=override added={added}");
                    }
                }

                if (table is null || table.IsEmpty)
                {
                    Trace.Error("pricing", "refresh failed reason=no-source-table");
                    return false;
                }

                SaveAtomically(path, table.ToCacheJson(string.Join("+", sources)));
                Trace.Info("pricing", $"refresh status=ok models={table.Rates.Count} source={string.Join("+", sources)}");
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

    private static async Task<PricingTable?> DownloadAsync(
        HttpClient http,
        string url,
        Func<string, DateTimeOffset, PricingTable> parse,
        string source,
        CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Trace.Error("pricing", $"source={source} status={(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var table = parse(json, DateTimeOffset.UtcNow);
            if (table.IsEmpty)
            {
                Trace.Error("pricing", $"source={source} reason=empty-table");
                return null;
            }

            return table;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Trace.Error("pricing", $"source={source} reason=timeout");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.Error("pricing", $"source={source} failed type={ex.GetType().Name}");
            return null;
        }
    }

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
