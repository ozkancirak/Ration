using Ration.Core.Settings;
using Ration.Core.Updates;

namespace Ration.App.Updates;

/// <summary>The app's single update service and version info.</summary>
internal static class AppUpdates
{
    private const string DefaultRepository = "https://github.com/ozkancirak/Ration";

    public static string RepositoryUrl =>
        Environment.GetEnvironmentVariable("RATION_UPDATE_REPOSITORY") is { Length: > 0 } value
            ? value
            : DefaultRepository;

    public static string CurrentVersion
    {
        get
        {
            var version = typeof(AppUpdates).Assembly.GetName().Version;
            return version is null ? "0.2.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    /// <summary>
    /// Does the stored result report a new version. A record that went stale after an update
    /// (the reported version is already the running one) does not count as new.
    /// </summary>
    public static bool HasNewerUpdate(UpdateCheckResult? result) =>
        result is { UpdateAvailable: true, IsInstalled: true } &&
        UpdateService.IsNewer(result.AvailableVersion, CurrentVersion);

    private const string PrereleaseKey = "updatePrerelease";

    /// <summary>Prereleases (beta) are also searched; off by default, the user turns it on.</summary>
    public static bool IncludePrerelease
    {
        get => SettingsStore.Default.GetBool(PrereleaseKey, false);
        set => SettingsStore.Default.Set(PrereleaseKey, value);
    }

    public static UpdateService Service { get; } =
        new(new VelopackUpdateSource(RepositoryUrl, () => IncludePrerelease), new SettingsUpdateStateStore(SettingsStore.Default));
}
