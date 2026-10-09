using Velopack;
using Velopack.Sources;
using Ration.Core.Updates;
using IUpdateSource = Ration.Core.Updates.IUpdateSource;

namespace Ration.App.Updates;

/// <summary>GitHub sürümlerinden Velopack ile güncelleme.</summary>
internal sealed class VelopackUpdateSource : IUpdateSource
{
    private readonly UpdateManager _manager;

    public VelopackUpdateSource(string repositoryUrl)
    {
        _manager = new UpdateManager(new GithubSource(repositoryUrl, accessToken: null, prerelease: false));
    }

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return update is null
            ? null
            : new AvailableUpdate(update.TargetFullRelease.Version.ToString(), update);
    }
}
