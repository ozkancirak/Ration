namespace Ration.Core.Model;

public enum SourceKind
{
    LocalFile,
    OAuth,
    ApiKey,
    Cookie,
    Cli
}

public enum WindowKind
{
    Session,
    Weekly,
    Monthly,
    Daily
}

public enum ProviderStatus
{
    Ok,
    Degraded,
    AuthRequired,
    NotInstalled,
    Error
}

public sealed record UsageWindow(
    WindowKind Kind,
    double Used,
    double? Limit,
    double Percent,
    DateTimeOffset? ResetsAt,
    // There can be several windows of the same kind (weekly overall / Opus / Sonnet).
    // Short name shown in the UI; if null, Kind is enough.
    string? Label = null,
    // Total length of the window (for the pace calculation). Null if the source does not provide it;
    // while null, the pace line is not shown and no made-up estimate is produced.
    TimeSpan? WindowLength = null,
    // Providers such as Antigravity return windows under two-level groups.
    string? GroupName = null,
    // The group description carries second-level context, such as model names.
    string? GroupDescription = null);

public sealed record CreditBalance(
    decimal RemainingCredits,
    decimal? TotalCredits,
    string Currency);

public sealed record ModelTokenUsage(
    string Model,
    long Tokens,
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheCreationTokens = 0);

public sealed record FreeModelUsage(
    int RequestsToday,
    DateTimeOffset UtcDay);

public sealed record CostReport(
    decimal TotalCost,
    string Currency,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    // Tokens are read exactly from the logs; TotalCost is an ESTIMATE and covers only models
    // whose price is known. Models with unknown prices are listed below so that
    // the missing cost does not silently look low.
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheCreationTokens = 0,
    // Reasoning tokens are a subset of OutputTokens; kept for information, and
    // not added a second time to the total or the cost.
    long ReasoningTokens = 0,
    IReadOnlyList<string>? ModelsWithoutPricing = null,
    IReadOnlyList<ModelTokenUsage>? Models = null,
    FreeModelUsage? FreeUsage = null,
    // false means the local scan included an event without a usable timestamp;
    // the UI must not label that total as an exact "last 30 days" period.
    bool PeriodKnown = true,
    // Token totals by local day (daily bar chart). Tokens, not cost.
    IReadOnlyList<Ration.Core.Cost.DailyTokens>? Daily = null)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

public sealed record UsageSnapshot(
    string ProviderId,
    IReadOnlyList<UsageWindow> Windows,
    CreditBalance? Credits,
    CostReport? Cost,
    ProviderStatus Status,
    SourceKind? ResolvedVia,
    DateTimeOffset FetchedAt,
    string? StaleReason,
    // Plan name reported by the provider ("plus", "max"...). For information only,
    // not part of the quota calculation; null if unknown.
    string? PlanName = null,
    // The state explained to the user for local modes that have no provider quota.
    string? StatusDetail = null,
    // For sources like OpenCode, only the names of configured providers are shown,
    // not credential values.
    IReadOnlyList<string>? ConfiguredProviders = null)
{
    /// <summary>The windows are the last good result, kept after a failed refresh (Degraded with a stale reason).</summary>
    public bool IsStale => Status == ProviderStatus.Degraded && StaleReason is not null;
}
