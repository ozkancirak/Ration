using Ration.Core.Model;

namespace Ration.Core.Abstractions;

/// <summary>
/// Bir kaynağın nihai sonuçtan önce güvenle gösterebileceği kısmi snapshot'lar.
/// Normal kaynaklar için ek sözleşme yoktur.
/// </summary>
public interface IProgressiveUsageSource : IUsageSource
{
    event Action<UsageSnapshot>? SnapshotUpdated;
}
