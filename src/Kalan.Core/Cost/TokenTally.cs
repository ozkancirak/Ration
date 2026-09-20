namespace Kalan.Core.Cost;

public sealed record ModelTokens(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    // Akıl yürütme / düşünme token'ları. Hem Codex (reasoning_output_tokens) hem
    // Claude (output_tokens_details.thinking_tokens) bunu OutputTokens'ın ALT KÜMESİ
    // olarak bildirir — bu yüzden toplama eklenmez, yalnızca görünürlük için tutulur.
    long ReasoningTokens = 0)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>
/// Model bazında token toplayıcı.
///
/// Token sayıları loglardan KESİN okunur — burada tahmin yoktur. Maliyet ayrı bir
/// katmandır (<see cref="PricingTable"/>), çünkü fiyatlar değişkendir ve eksik
/// fiyat bilgisiyle üretilen "kesin" maliyet, hiç maliyet göstermemekten kötüdür.
/// </summary>
public sealed class TokenTally
{
    private readonly Dictionary<string, long[]> _byModel = new(StringComparer.OrdinalIgnoreCase);

    private const int Input = 0;
    private const int Output = 1;
    private const int CacheRead = 2;
    private const int CacheCreate = 3;
    private const int Reasoning = 4;

    public int EntryCount { get; private set; }

    public void Add(
        string? model,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens,
        long reasoningTokens = 0)
    {
        var key = string.IsNullOrWhiteSpace(model) ? "(bilinmeyen model)" : model;

        if (!_byModel.TryGetValue(key, out var bucket))
        {
            bucket = new long[5];
            _byModel[key] = bucket;
        }

        bucket[Input] += inputTokens;
        bucket[Output] += outputTokens;
        bucket[CacheRead] += cacheReadTokens;
        bucket[CacheCreate] += cacheCreationTokens;
        bucket[Reasoning] += reasoningTokens;

        EntryCount++;
    }

    public IReadOnlyList<ModelTokens> Models =>
        _byModel
            .Select(kv => new ModelTokens(
                kv.Key,
                kv.Value[Input],
                kv.Value[Output],
                kv.Value[CacheRead],
                kv.Value[CacheCreate],
                kv.Value[Reasoning]))
            .OrderByDescending(m => m.TotalTokens)
            .ToList();

    public long TotalInputTokens => _byModel.Values.Sum(v => v[Input]);

    public long TotalOutputTokens => _byModel.Values.Sum(v => v[Output]);

    public long TotalCacheReadTokens => _byModel.Values.Sum(v => v[CacheRead]);

    public long TotalCacheCreationTokens => _byModel.Values.Sum(v => v[CacheCreate]);

    public long TotalReasoningTokens => _byModel.Values.Sum(v => v[Reasoning]);

    public bool IsEmpty => _byModel.Count == 0;
}

public sealed record CostScanResult(
    TokenTally Tally,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    int FilesScanned,
    string? Note = null);
