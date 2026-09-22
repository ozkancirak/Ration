namespace Ration.Core.Abstractions;

public interface IUsageProvider
{
    string Id { get; }                            // "claude", "codex"
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }
    IReadOnlyList<IUsageSource> Sources { get; }  // Priority order: first successful source wins
}

public sealed record ProviderCapabilities(
    bool SupportsSessionWindow,
    bool SupportsWeeklyWindow,
    bool SupportsMonthlyWindow,
    bool SupportsCredits,
    bool SupportsCostReport);
