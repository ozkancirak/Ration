namespace Ration.Core.Updates;

/// <summary>The update the source found. <paramref name="Package"/> is source-specific data that is closed to the outside.</summary>
public sealed record AvailableUpdate(string Version, object? Package = null);

/// <summary>
/// Where update packages come from. The real app uses Velopack; tests supply a fake source,
/// so no test touches the network or the install directory.
/// </summary>
public interface IUpdateSource
{
    /// <summary>Is the app running from an install (not the portable zip or development)?</summary>
    bool IsInstalled { get; }

    /// <summary>null if there is no new version.</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads and verifies the package; does not touch the app. <paramref name="progress"/> reports percent.</summary>
    Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Schedules the downloaded package to be applied when this process exits, and the app to be reopened
    /// if <paramref name="restart"/> is true. It does not close the process itself; the caller does the exit
    /// so that the tray icon is cleaned up properly.
    /// </summary>
    void ApplyOnExit(AvailableUpdate update, bool restart);
}
