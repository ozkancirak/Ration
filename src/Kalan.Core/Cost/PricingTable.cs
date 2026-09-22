using System.Globalization;
using System.Text.Json;
using KalanTrace = Kalan.Core.Diagnostics.Trace;
using Kalan.Core.Model;
using Kalan.Core.Providers;

namespace Kalan.Core.Cost;

/// <summary>Milyon token başına fiyat (sağlayıcının para biriminde, varsayılan USD).</summary>
public sealed record ModelRate(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion);

/// <summary>
/// Model fiyat tablosu.
///
/// Sağlayıcı fiyatları sık değişir; kodun içine gömülen bir fiyat listesi zamanla
/// sessizce yanlışlaşır ve kullanıcı yanlış maliyeti doğru sanır. Fiyatlar
/// haftalık kaynak cache'inden ve kullanıcının override dosyasından okunur:
///
///   %LOCALAPPDATA%\Kalan\pricing.json
///   %LOCALAPPDATA%\Kalan\pricing-overrides.json
///   {
///     "claude-sonnet": { "input": 3.00, "output": 15.00, "cacheRead": 0.30, "cacheWrite": 3.75 },
///     "gpt-6":         { "input": 1.25, "output": 10.00 }
///   }
///
/// Anahtar eşleşmesi: önce birebir, sonra en uzun önek. Böylece "claude-sonnet"
/// girdisi "claude-sonnet-4-5-20260101" modelini de karşılar.
/// Fiyatı bulunamayan model maliyete KATILMAZ ve ayrıca raporlanır.
/// </summary>
public sealed class PricingTable
{
    private const decimal Million = 1_000_000m;
    private readonly Dictionary<string, ModelRate> _rates;

    public string Currency { get; }
    public DateTimeOffset? DownloadedAt { get; }

    public bool IsEmpty => _rates.Count == 0;

    public PricingTable(
        IDictionary<string, ModelRate>? rates = null,
        string currency = "USD",
        DateTimeOffset? downloadedAt = null)
    {
        _rates = rates is null
            ? new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ModelRate>(rates, StringComparer.OrdinalIgnoreCase);

        Currency = currency;
        DownloadedAt = downloadedAt;
    }

    public static PricingTable Empty { get; } = new();

    public static string DefaultPath => Path.Combine(KnownPaths.CacheDir, "pricing.json");

    /// <summary>Dosya yoksa ya da bozuksa boş tablo döner — asla exception atmaz.</summary>
    public static PricingTable LoadOrEmpty(string? path = null)
    {
        var useDefaultChain = path is null;
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return useDefaultChain
                ? LoadOrEmpty(KnownPaths.PricingOverridesFile)
                : Empty;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return Empty;

            var rates = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
            var currency = "USD";
            DateTimeOffset? downloadedAt = null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("currency") && property.Value.ValueKind == JsonValueKind.String)
                {
                    currency = property.Value.GetString() ?? "USD";
                    continue;
                }

                if (property.NameEquals("_metadata") &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    downloadedAt = ReadTimestamp(property.Value, "downloadedAtUtc");
                    continue;
                }

                if (property.NameEquals("downloadedAtUtc"))
                {
                    downloadedAt = ReadTimestamp(document.RootElement, "downloadedAtUtc");
                    continue;
                }

                if (property.Name.StartsWith('_')) continue;

                if (property.Value.ValueKind != JsonValueKind.Object) continue;

                if (TryReadNormalizedRate(property.Value, out var rate))
                {
                    rates[property.Name] = rate;
                }
            }

            var table = new PricingTable(rates, currency, downloadedAt);
            if (!useDefaultChain) return table;

            var merged = table.MergeMissing(
                LoadOrEmpty(KnownPaths.PricingOverridesFile),
                out var added);
            if (added > 0)
            {
                KalanTrace.Info("pricing", $"source=overrides added={added}");
            }

            return merged;
        }
        catch (JsonException) { return Empty; }
        catch (IOException) { return Empty; }
        catch (UnauthorizedAccessException) { return Empty; }
    }

    public ModelRate? Find(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        if (_rates.TryGetValue(model, out var exact)) return exact;

        ModelRate? best = null;
        var bestLength = 0;

        foreach (var (key, rate) in _rates)
        {
            if (key.Length <= bestLength) continue;
            if (!model.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;

            best = rate;
            bestLength = key.Length;
        }

        return best;
    }

    /// <summary>
    /// Bu tablodaki fiyatları koruyup fallback tablosundan yalnızca eksikleri ekler.
    /// Böylece kaynak zincirinde ilk bulunan kayıt kazanır.
    /// </summary>
    public PricingTable MergeMissing(PricingTable fallback, out int added)
    {
        var rates = new Dictionary<string, ModelRate>(_rates, StringComparer.OrdinalIgnoreCase);
        added = 0;
        foreach (var (model, rate) in fallback._rates)
        {
            if (rates.TryAdd(model, rate)) added++;
        }

        return new PricingTable(rates, Currency, DownloadedAt);
    }

    /// <summary>
    /// LiteLLM fiyatlarını uygulamanın per-million formatına çevirir.
    /// Sağlayıcı/region öneklerinin sonundaki model adı da alias olarak eklenir;
    /// böylece loglardaki "claude-sonnet-..." ve "gpt-..." adları eşleşir.
    /// </summary>
    public static PricingTable FromLiteLlmJson(
        string json,
        DateTimeOffset downloadedAt)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var sourceRates = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (TryReadLiteLlmRate(property.Value, out var rate))
            {
                sourceRates[property.Name] = rate;
            }
        }

        if (sourceRates.Count == 0) return Empty;

        var rates = new Dictionary<string, ModelRate>(sourceRates, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, rate) in sourceRates)
        {
            foreach (var alias in Aliases(key))
            {
                rates.TryAdd(alias, rate);
            }
        }

        return new PricingTable(rates, "USD", downloadedAt);
    }

    /// <summary>
    /// models.dev /api.json şemasını okur: provider.models[model].cost.
    /// cost değerleri README'deki sözleşmeye göre milyon token başına USD'dir.
    /// </summary>
    public static PricingTable FromModelsDevJson(
        string json,
        DateTimeOffset downloadedAt)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var rates = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in document.RootElement.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object ||
                !provider.Value.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var model in models.EnumerateObject())
            {
                if (model.Value.ValueKind != JsonValueKind.Object ||
                    !TryReadModelsDevRate(model.Value, out var rate))
                {
                    continue;
                }

                AddRateWithAliases(rates, model.Name, rate);
                if (model.Value.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    AddRateWithAliases(rates, id.GetString()!, rate);
                }
            }
        }

        return rates.Count == 0
            ? Empty
            : new PricingTable(rates, "USD", downloadedAt);
    }

    internal IReadOnlyDictionary<string, ModelRate> Rates => _rates;

    internal static DateTimeOffset? TryReadDownloadedAt(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (document.RootElement.TryGetProperty("_metadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object)
            {
                return ReadTimestamp(metadata, "downloadedAtUtc");
            }

            return ReadTimestamp(document.RootElement, "downloadedAtUtc");
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal string ToCacheJson(string source)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_metadata"] = new Dictionary<string, string>
            {
                ["source"] = source,
                ["downloadedAtUtc"] = (DownloadedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            },
            ["currency"] = Currency,
        };

        foreach (var (model, rate) in _rates)
        {
            if (model.StartsWith('_')) continue;
            payload[model] = new
            {
                input = rate.InputPerMillion,
                output = rate.OutputPerMillion,
                cacheRead = rate.CacheReadPerMillion,
                cacheWrite = rate.CacheWritePerMillion,
            };
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = false,
        });
    }

    private static decimal ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0m;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0m;
    }

    private static bool TryReadNormalizedRate(JsonElement element, out ModelRate rate)
    {
        var hasAny = HasProperty(element, "input") ||
                     HasProperty(element, "output") ||
                     HasProperty(element, "cacheRead") ||
                     HasProperty(element, "cacheWrite");
        if (!hasAny)
        {
            rate = default!;
            return false;
        }

        rate = new ModelRate(
            ReadDecimal(element, "input"),
            ReadDecimal(element, "output"),
            ReadDecimal(element, "cacheRead"),
            ReadDecimal(element, "cacheWrite"));
        return true;
    }

    private static bool TryReadLiteLlmRate(JsonElement element, out ModelRate rate)
    {
        var input = ReadLiteLlmDecimal(element, "input_cost_per_token", out var hasInput);
        var output = ReadLiteLlmDecimal(element, "output_cost_per_token", out var hasOutput);
        var cacheRead = ReadLiteLlmDecimal(element, "cache_read_input_token_cost", out var hasCacheRead);
        var cacheWrite = ReadLiteLlmDecimal(element, "cache_creation_input_token_cost", out var hasCacheWrite);

        if (!hasCacheWrite)
        {
            cacheWrite = ReadLiteLlmDecimal(element, "cache_write_input_token_cost", out hasCacheWrite);
        }

        if (!hasInput && !hasOutput && !hasCacheRead && !hasCacheWrite)
        {
            rate = default!;
            return false;
        }

        rate = new ModelRate(input * Million, output * Million, cacheRead * Million, cacheWrite * Million);
        return true;
    }

    private static bool TryReadModelsDevRate(JsonElement element, out ModelRate rate)
    {
        if (!element.TryGetProperty("cost", out var cost) ||
            cost.ValueKind != JsonValueKind.Object)
        {
            rate = default!;
            return false;
        }

        var input = ReadDecimal(cost, "input");
        var output = ReadDecimal(cost, "output");
        var cacheRead = ReadDecimal(cost, "cache_read");
        var cacheWrite = ReadDecimal(cost, "cache_write");
        var hasAny = HasProperty(cost, "input") ||
                     HasProperty(cost, "output") ||
                     HasProperty(cost, "cache_read") ||
                     HasProperty(cost, "cache_write");
        if (!hasAny)
        {
            rate = default!;
            return false;
        }

        rate = new ModelRate(input, output, cacheRead, cacheWrite);
        return true;
    }

    private static void AddRateWithAliases(
        IDictionary<string, ModelRate> rates,
        string model,
        ModelRate rate)
    {
        if (string.IsNullOrWhiteSpace(model)) return;

        rates.TryAdd(model, rate);
        foreach (var alias in Aliases(model)) rates.TryAdd(alias, rate);
    }

    private static decimal ReadLiteLlmDecimal(
        JsonElement element,
        string name,
        out bool present)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
        {
            present = false;
            return 0m;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            present = value.TryGetDecimal(out var number);
            return present ? number : 0m;
        }

        if (!decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            present = false;
            return 0m;
        }

        present = true;
        return parsed;
    }

    private static bool HasProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.Number or JsonValueKind.String;

    private static IEnumerable<string> Aliases(string key)
    {
        for (var index = 0; index < key.Length; index++)
        {
            if (key[index] is not ('/' or '.')) continue;
            var alias = key[(index + 1)..];
            if (alias.Length > 0) yield return alias;
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }
}

public static class CostEstimator
{
    private const decimal Million = 1_000_000m;

    /// <summary>
    /// Fiyatı bilinen modellerin maliyetini toplar. Bilinmeyen modeller toplama
    /// katılmaz ve <see cref="CostReport.ModelsWithoutPricing"/> ile raporlanır.
    /// </summary>
    public static CostReport Estimate(CostScanResult scan, PricingTable pricing)
    {
        var total = 0m;
        var unpriced = new List<string>();

        foreach (var model in scan.Tally.Models)
        {
            var rate = pricing.Find(model.Model);

            if (rate is null)
            {
                if (model.TotalTokens > 0)
                {
                    unpriced.Add(model.Model);
                    KalanTrace.Info("pricing", $"model-unpriced model={model.Model}");
                }
                continue;
            }

            total +=
                model.InputTokens / Million * rate.InputPerMillion +
                model.OutputTokens / Million * rate.OutputPerMillion +
                model.CacheReadTokens / Million * rate.CacheReadPerMillion +
                model.CacheCreationTokens / Million * rate.CacheWritePerMillion;
        }

        return new CostReport(
            TotalCost: Math.Round(total, 4),
            Currency: pricing.Currency,
            PeriodStart: scan.PeriodStart,
            PeriodEnd: scan.PeriodEnd,
            InputTokens: scan.Tally.TotalInputTokens,
            OutputTokens: scan.Tally.TotalOutputTokens,
            CacheReadTokens: scan.Tally.TotalCacheReadTokens,
            CacheCreationTokens: scan.Tally.TotalCacheCreationTokens,
            ReasoningTokens: scan.Tally.TotalReasoningTokens,
            ModelsWithoutPricing: unpriced,
            Models: scan.Tally.Models
                .Where(model => model.TotalTokens > 0)
                .Select(model => new ModelTokenUsage(
                    model.Model,
                    model.TotalTokens,
                    model.InputTokens,
                    model.OutputTokens,
                    model.CacheReadTokens,
                    model.CacheCreationTokens))
                .ToArray(),
            PeriodKnown: scan.PeriodKnown);
    }

    /// <summary>
    /// OpenCode gibi token raporunu zaten üretmiş kaynaklar için aynı fiyat
    /// hesabını uygular. Model kırılımı yoksa güvenilir bir API karşılığı
    /// çıkarılamaz; bu durumda yalnızca token sayıları korunur.
    /// </summary>
    public static CostReport Estimate(CostReport usage, PricingTable pricing)
        => Estimate(usage, pricing, aliases: null);

    public static CostReport Estimate(
        CostReport usage,
        PricingTable pricing,
        ModelAliasTable? aliases)
    {
        var total = 0m;
        var unpriced = new List<string>();

        foreach (var model in usage.Models ?? Array.Empty<ModelTokenUsage>())
        {
            var rate = pricing.Find(aliases?.Resolve(model.Model) ?? model.Model);
            if (rate is null)
            {
                if (model.Tokens > 0)
                {
                    unpriced.Add(model.Model);
                    KalanTrace.Info("pricing", $"model-unpriced model={model.Model}");
                }
                continue;
            }

            total +=
                model.InputTokens / Million * rate.InputPerMillion +
                model.OutputTokens / Million * rate.OutputPerMillion +
                model.CacheReadTokens / Million * rate.CacheReadPerMillion +
                model.CacheCreationTokens / Million * rate.CacheWritePerMillion;
        }

        return usage with
        {
            TotalCost = Math.Round(total, 4),
            Currency = pricing.Currency,
            ModelsWithoutPricing = usage.Models is null ? usage.ModelsWithoutPricing : unpriced,
        };
    }
}
