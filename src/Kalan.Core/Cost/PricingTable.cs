using System.Globalization;
using System.Text.Json;
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
/// KASITLI OLARAK BOŞ GELİR. Sağlayıcı fiyatları sık değişir; kodun içine gömülen
/// bir fiyat listesi zamanla sessizce yanlışlaşır ve kullanıcı yanlış maliyeti
/// doğru sanır. Bunun yerine fiyatlar kullanıcının kendi dosyasından okunur:
///
///   %LOCALAPPDATA%\Kalan\pricing.json
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
    private readonly Dictionary<string, ModelRate> _rates;

    public string Currency { get; }

    public bool IsEmpty => _rates.Count == 0;

    public PricingTable(IDictionary<string, ModelRate>? rates = null, string currency = "USD")
    {
        _rates = rates is null
            ? new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ModelRate>(rates, StringComparer.OrdinalIgnoreCase);

        Currency = currency;
    }

    public static PricingTable Empty { get; } = new();

    public static string DefaultPath => Path.Combine(KnownPaths.CacheDir, "pricing.json");

    /// <summary>Dosya yoksa ya da bozuksa boş tablo döner — asla exception atmaz.</summary>
    public static PricingTable LoadOrEmpty(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return Empty;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return Empty;

            var rates = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
            var currency = "USD";

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("currency") && property.Value.ValueKind == JsonValueKind.String)
                {
                    currency = property.Value.GetString() ?? "USD";
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Object) continue;

                rates[property.Name] = new ModelRate(
                    ReadDecimal(property.Value, "input"),
                    ReadDecimal(property.Value, "output"),
                    ReadDecimal(property.Value, "cacheRead"),
                    ReadDecimal(property.Value, "cacheWrite"));
            }

            return new PricingTable(rates, currency);
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
                if (model.TotalTokens > 0) unpriced.Add(model.Model);
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
            ModelsWithoutPricing: unpriced);
    }
}
