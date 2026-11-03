using Ration.Core.Diagnostics;

namespace Ration.Core.Updates;

public sealed record UpdateCheckResult(
    bool IsInstalled,
    bool UpdateAvailable,
    string? AvailableVersion,
    bool Succeeded,
    DateTimeOffset CheckedAt);

public enum UpdateDownloadResult
{
    Downloaded,
    /// <summary>The check found no new version (or the source no longer offers it).</summary>
    NothingToDownload,
    /// <summary>Network, disk or verification error; the app is unaffected and it can be retried.</summary>
    Failed,
    /// <summary>Another download is in progress.</summary>
    Busy,
}

/// <summary>Where the result of the last check is stored.</summary>
public interface IUpdateStateStore
{
    UpdateCheckResult? Load();
    void Save(UpdateCheckResult result);
}

/// <summary>
/// Update check. The source and storage are injected; the package is downloaded only
/// by user action.
/// </summary>
public sealed class UpdateService
{
    private readonly IUpdateSource _source;
    private readonly IUpdateStateStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private AvailableUpdate? _found;
    private bool _downloading;

    public UpdateService(IUpdateSource source, IUpdateStateStore store, Func<DateTimeOffset>? clock = null)
    {
        _source = source;
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public UpdateCheckResult? LastResult => _store.Load();

    /// <summary>Fired after every check (successful or not); can come from any thread.</summary>
    public event Action<UpdateCheckResult>? ResultChanged;

    /// <summary>Is it running from an install; if not, the check is meaningless.</summary>
    public bool IsInstalled => _source.IsInstalled;

    /// <summary>The found update has been downloaded and is ready to be applied.</summary>
    public bool IsDownloaded { get; private set; }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = _clock();
        UpdateCheckResult result;
        try
        {
            if (!_source.IsInstalled)
            {
                result = new(false, false, null, false, checkedAt);
            }
            else
            {
                var update = await _source.CheckAsync(cancellationToken).ConfigureAwait(false);
                Remember(update);
                result = update is null
                    ? new(true, false, null, true, checkedAt)
                    : new(true, true, update.Version, true, checkedAt);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.Error("updates", $"check failed type={ex.GetType().Name}");
            result = new(true, false, null, false, checkedAt);
        }

        Save(result);
        ResultChanged?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Downloads the found update. For an "update available" notice left over from a previous session, the source
    /// object is not in memory; in that case it is found again first. A failure does not affect the app.
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_downloading) return UpdateDownloadResult.Busy;
        _downloading = true;
        try
        {
            var update = _found ?? await _source.CheckAsync(cancellationToken).ConfigureAwait(false);
            Remember(update);
            if (update is null) return UpdateDownloadResult.NothingToDownload;

            // The source may report a percentage outside 0-100; a valid value always goes to the UI.
            await _source.DownloadAsync(update, percent => progress?.Report(Math.Clamp(percent, 0, 100)), cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(100);
            IsDownloaded = true;
            return UpdateDownloadResult.Downloaded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.Error("updates", $"download failed type={ex.GetType().Name}");
            IsDownloaded = false;
            return UpdateDownloadResult.Failed;
        }
        finally
        {
            _downloading = false;
        }
    }

    /// <summary>
    /// Schedules the downloaded update to be applied on exit. If it returns true, the caller
    /// must exit the app; the update is installed after exit and the app is reopened.
    /// </summary>
    public bool ApplyOnExitAndRestart()
    {
        if (!IsDownloaded || _found is null) return false;

        try
        {
            _source.ApplyOnExitAndRestart(_found);
            return true;
        }
        catch (Exception ex)
        {
            Trace.Error("updates", $"apply failed type={ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// The stored "update available" notice stays stale after an update: is the version really newer than the
    /// current one. SemVer order: a prerelease suffix (-beta.2) is older than the stable version with the same
    /// number, and prereleases are compared identifier by identifier among themselves (beta.2 &gt; beta.1).
    /// </summary>
    public static bool IsNewer(string? available, string current)
    {
        if (!TryParse(available, out var a, out var aPre) || !TryParse(current, out var c, out var cPre)) return false;
        var order = a.CompareTo(c);
        if (order != 0) return order > 0;
        if (aPre is null) return cPre is not null;
        return cPre is not null && ComparePreRelease(aPre, cPre) > 0;
    }

    private static int ComparePreRelease(string left, string right)
    {
        var l = left.Split('.');
        var r = right.Split('.');
        for (var i = 0; i < Math.Min(l.Length, r.Length); i++)
        {
            var leftNumeric = int.TryParse(l[i], out var ln);
            var rightNumeric = int.TryParse(r[i], out var rn);
            int order = leftNumeric && rightNumeric ? ln.CompareTo(rn)
                : leftNumeric ? -1   // a numeric identifier is lower than an alphanumeric one
                : rightNumeric ? 1
                : string.CompareOrdinal(l[i], r[i]);
            if (order != 0) return order;
        }

        return l.Length.CompareTo(r.Length);
    }

    private static bool TryParse(string? text, out Version version, out string? preRelease)
    {
        version = new Version();
        preRelease = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().TrimStart('v').Split('-', 2);
        if (parts.Length == 2) preRelease = parts[1];
        return Version.TryParse(parts[0], out version!);
    }

    private void Remember(AvailableUpdate? update)
    {
        if (update?.Version != _found?.Version) IsDownloaded = false;
        _found = update;
    }

    private void Save(UpdateCheckResult result)
    {
        try { _store.Save(result); }
        catch (Exception ex) { Trace.Error("updates", $"last-check write failed type={ex.GetType().Name}"); }
    }
}
