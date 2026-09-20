using Kalan.Core.Model;

namespace Kalan.Core.Abstractions;

public interface IUsageSource
{
    SourceKind Kind { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<UsageSnapshot> FetchAsync(CancellationToken ct = default);
}
