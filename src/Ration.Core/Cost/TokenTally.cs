namespace Ration.Core.Cost;

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
    // Yerel güne göre model kırılımı: günlük grafik ve tek taramadan "bugün" raporu için.
    private readonly Dictionary<DateOnly, TokenTally> _byDay = new();

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
        long reasoningTokens = 0,
        DateTimeOffset? at = null)
    {
        var key = string.IsNullOrWhiteSpace(model) ? L.T("(unknown model)", "(bilinmeyen model)") : model;

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

        // Zamanı bilinmeyen olay gün kovasına girmez (grafikte ve "bugün"de yok).
        if (at is { } time)
        {
            var day = DateOnly.FromDateTime(time.ToLocalTime().DateTime);
            if (!_byDay.TryGetValue(day, out var dayTally))
            {
                dayTally = new TokenTally();
                _byDay[day] = dayTally;
            }
            dayTally.Add(model, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, reasoningTokens);
        }

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

    public IReadOnlyList<DailyTokens> Daily =>
        _byDay.OrderBy(kv => kv.Key)
            .Select(kv => new DailyTokens(kv.Key, kv.Value.Models.Sum(m => m.TotalTokens)))
            .ToList();

    /// <summary>
    /// <paramref name="day"/> ve sonrasındaki olaylar. 30 günlük taramadan "bugün" raporunu
    /// türetir; aynı dosyaları ikinci kez okumaya gerek kalmaz.
    /// </summary>
    public TokenTally SinceDay(DateOnly day)
    {
        var result = new TokenTally();
        foreach (var (_, dayTally) in _byDay.Where(kv => kv.Key >= day))
        {
            foreach (var m in dayTally.Models)
            {
                result.Add(m.Model, m.InputTokens, m.OutputTokens, m.CacheReadTokens, m.CacheCreationTokens, m.ReasoningTokens);
            }
        }
        return result;
    }

    public bool IsEmpty => _byModel.Count == 0;
}

public sealed record DailyTokens(DateOnly Day, long Tokens);

public sealed record CostScanResult(
    TokenTally Tally,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    int FilesScanned,
    string? Note = null)
{
    /// <summary>
    /// false when one or more counted events had no usable event timestamp.
    /// Such a result must not be presented as an exact calendar period.
    /// </summary>
    public bool PeriodKnown { get; init; } = true;
}
