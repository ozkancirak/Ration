namespace Ration.Core.Cost;

public sealed record ModelTokens(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    // Reasoning / thinking tokens. Both Codex (reasoning_output_tokens) and
    // Claude (output_tokens_details.thinking_tokens) report this as a SUBSET of OutputTokens
    // — so it is not added to the total, only kept for visibility.
    long ReasoningTokens = 0)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>
/// Per-model token accumulator.
///
/// Token counts are read EXACTLY from the logs — there is no estimation here. Cost is a separate
/// layer (<see cref="PricingTable"/>), because prices change and an "exact" cost produced from
/// incomplete price information is worse than showing no cost at all.
/// </summary>
public sealed class TokenTally
{
    private readonly Dictionary<string, long[]> _byModel = new(StringComparer.OrdinalIgnoreCase);
    // Per-model breakdown by local day: for the daily chart and a "today" report from a single scan.
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

        // An event with an unknown time does not go into a day bucket (not in the chart and not in "today").
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
    /// Events on and after <paramref name="day"/>. Derives the "today" report from the 30-day scan;
    /// there is no need to read the same files a second time.
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
