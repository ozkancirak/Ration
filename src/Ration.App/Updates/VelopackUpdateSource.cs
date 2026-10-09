using Velopack;
using Velopack.Sources;
using Ration.Core.Updates;
using IUpdateSource = Ration.Core.Updates.IUpdateSource;

namespace Ration.App.Updates;

/// <summary>GitHub sürümlerinden Velopack ile güncelleme.</summary>
internal sealed class VelopackUpdateSource : IUpdateSource
{
    private readonly string _repositoryUrl;
    private readonly Func<bool> _includePrerelease;

    /// <summary>Bulunan güncellemeyi hangi yöneticinin bulduğu; indirme ve uygulama aynı kaynağı kullanmalı.</summary>
    private sealed record Found(UpdateManager Manager, UpdateInfo Info);

    /// <param name="includePrerelease">Her denetimde okunur; kanal değişince yeniden başlatmaya gerek kalmaz.</param>
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

    public void ApplyOnExitAndRestart(AvailableUpdate update)
    {
        var found = (Found)update.Package!;
        found.Manager.WaitExitThenApplyUpdates(found.Info.TargetFullRelease, silent: true, restart: true);
    }

    /// <summary>
    /// RATION_UPDATE_REPOSITORY bir klasör gösteriyorsa (vpk pack çıktısı) güncellemeler oradan gelir;
    /// güncelleme yolunu GitHub'a çıkmadan sınamak için.
    /// </summary>
    private UpdateManager CreateManager(bool prerelease) =>
        Directory.Exists(_repositoryUrl)
            ? new(new SimpleFileSource(new DirectoryInfo(_repositoryUrl)))
            : new(new GithubSource(_repositoryUrl, accessToken: null, prerelease: prerelease));
}
