using Velopack;
using Velopack.Sources;
using Ration.Core.Updates;
using IUpdateSource = Ration.Core.Updates.IUpdateSource;

namespace Ration.App.Updates;

/// <summary>Update from GitHub releases with Velopack.</summary>
internal sealed class VelopackUpdateSource : IUpdateSource
{
    private readonly string _repositoryUrl;
    private readonly Func<bool> _includePrerelease;

    /// <summary>Which manager found the update; download and apply must use the same source.</summary>
    private sealed record Found(UpdateManager Manager, UpdateInfo Info);

    /// <param name="includePrerelease">Read on every check; no restart is needed when the channel changes.</param>
    public VelopackUpdateSource(string repositoryUrl, Func<bool> includePrerelease)
    {
        _repositoryUrl = repositoryUrl;
        _includePrerelease = includePrerelease;
    }

    public bool IsInstalled => CreateManager(prerelease: false).IsInstalled;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        var manager = CreateManager(_includePrerelease());
        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return update is null
            ? null
            : new AvailableUpdate(update.TargetFullRelease.Version.ToString(), new Found(manager, update));
    }

    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken)
    {
        var found = (Found)update.Package!;
        return found.Manager.DownloadUpdatesAsync(found.Info, progress, cancellationToken);
    }

    public void ApplyOnExit(AvailableUpdate update, bool restart)
    {
        var found = (Found)update.Package!;
        found.Manager.WaitExitThenApplyUpdates(found.Info.TargetFullRelease, silent: true, restart: restart);
    }

    /// <summary>
    /// If RATION_UPDATE_REPOSITORY points to a folder (vpk pack output), updates come from there;
    /// to test the update path without going to GitHub.
    /// </summary>
    private UpdateManager CreateManager(bool prerelease) =>
        Directory.Exists(_repositoryUrl)
            ? new(new SimpleFileSource(new DirectoryInfo(_repositoryUrl)))
            : new(new GithubSource(_repositoryUrl, accessToken: null, prerelease: prerelease));
}
