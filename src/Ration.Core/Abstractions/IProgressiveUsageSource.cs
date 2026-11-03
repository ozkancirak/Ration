using Ration.Core.Model;

namespace Ration.Core.Abstractions;

/// <summary>
/// Partial snapshots that a source can safely show before its final result.
/// Ordinary sources have no extra contract.
/// </summary>
public interface IProgressiveUsageSource : IUsageSource
{
    event Action<UsageSnapshot>? SnapshotUpdated;
}
