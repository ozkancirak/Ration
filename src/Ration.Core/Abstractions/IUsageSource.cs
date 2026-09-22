using Ration.Core.Model;

namespace Ration.Core.Abstractions;

public interface IUsageSource
{
    SourceKind Kind { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<UsageSnapshot> FetchAsync(CancellationToken ct = default);
}
