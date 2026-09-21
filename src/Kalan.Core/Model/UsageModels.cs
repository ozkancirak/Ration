namespace Kalan.Core.Model;

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
    // Aynı türden birden fazla pencere olabilir (haftalık genel / Opus / Sonnet).
    // UI'da gösterilecek kısa ad; null ise Kind yeterlidir.
    string? Label = null,
    // Pencerenin toplam uzunluğu (tempo hesabı için). Kaynak vermiyorsa null;
    // null iken tempo satırı gösterilmez, uydurma tahmin üretilmez.
    TimeSpan? WindowLength = null);

public sealed record CreditBalance(
    decimal RemainingCredits,
    decimal? TotalCredits,
    string Currency);

public sealed record CostReport(
    decimal TotalCost,
    string Currency,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    // Token'lar loglardan kesin okunur; TotalCost bir TAHMİNdır ve yalnızca fiyatı
    // bilinen modelleri kapsar. Fiyatı bilinmeyen modeller aşağıda listelenir ki
    // eksik maliyet sessizce düşük görünmesin.
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheCreationTokens = 0,
    // Akıl yürütme token'ları OutputTokens'ın alt kümesidir; bilgi amaçlı tutulur,
    // toplama ve maliyete ikinci kez eklenmez.
    long ReasoningTokens = 0,
    IReadOnlyList<string>? ModelsWithoutPricing = null);

public sealed record UsageSnapshot(
    string ProviderId,
    IReadOnlyList<UsageWindow> Windows,
    CreditBalance? Credits,
    CostReport? Cost,
    ProviderStatus Status,
    SourceKind? ResolvedVia,
    DateTimeOffset FetchedAt,
    string? StaleReason,
    // Sağlayıcının bildirdiği plan adı ("plus", "max"...). Bilgi amaçlıdır,
    // kota hesabına girmez; bilinmiyorsa null.
    string? PlanName = null);
