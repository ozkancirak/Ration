using Velopack;
using Velopack.Sources;
using Microsoft.Win32;
using Ration.Core.Diagnostics;

namespace Ration.App.Views;

public sealed record UpdateCheckResult(
    bool IsInstalled,
    bool UpdateAvailable,
    string? AvailableVersion,
    bool Succeeded,
    DateTimeOffset CheckedAt);

/// <summary>
/// Güncelleme yalnızca kontrol edilir. İndirme/uygulama için ayrıca kullanıcı eylemi
/// eklenene kadar hiçbir paket indirilmez.
/// </summary>
public static class UpdateService
{
    private const string SettingsKeyPath = @"Software\Ration";
    private const string LastCheckValueName = "UpdatesLastCheckedUtc";
    private const string LastSucceededValueName = "UpdatesLastSucceeded";
    private const string UpdateAvailableValueName = "UpdatesUpdateAvailable";
    private const string AvailableVersionValueName = "UpdatesAvailableVersion";
    private const string IsInstalledValueName = "UpdatesIsInstalled";
    private const string DefaultRepository = "https://github.com/ozkancirak/ration";

    public static string RepositoryUrl =>
        Environment.GetEnvironmentVariable("RATION_UPDATE_REPOSITORY") is { Length: > 0 } value
            ? value
            : DefaultRepository;

    public static string CurrentVersion
    {
        get
        {
            var version = typeof(UpdateService).Assembly.GetName().Version;
            return version is null ? "0.2.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    public static UpdateCheckResult? LastResult
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
                var value = key?.GetValue(LastCheckValueName) as string;
                if (!DateTimeOffset.TryParse(value, out var checkedAt)) return null;

                var succeeded = key?.GetValue(LastSucceededValueName) is int success && success != 0;
                var updateAvailable = key?.GetValue(UpdateAvailableValueName) is int available && available != 0;
                var installed = key?.GetValue(IsInstalledValueName) is int isInstalled && isInstalled != 0;
                var version = key?.GetValue(AvailableVersionValueName) as string;

                return new UpdateCheckResult(installed, updateAvailable, version, succeeded, checkedAt);
            }
            catch { return null; }
        }
    }

    public static DateTimeOffset? LastCheckedAt => LastResult?.CheckedAt;

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        var checkedAt = DateTimeOffset.UtcNow;
        UpdateCheckResult result;
        try
        {
            var manager = new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

            if (!manager.IsInstalled)
            {
                result = new(false, false, null, false, checkedAt);
                SaveResult(result);
                return result;
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (update is null)
            {
                result = new(true, false, null, true, checkedAt);
                SaveResult(result);
                return result;
            }

            result = new(
                true,
                true,
                update.TargetFullRelease.Version.ToString(),
                true,
                checkedAt);
            SaveResult(result);
            return result;
        }
        catch (Exception ex)
        {
            Trace.Error("updates", $"check failed type={ex.GetType().Name}");
            result = new(true, false, null, false, checkedAt);
            SaveResult(result);
            return result;
        }
    }

    private static void SaveResult(UpdateCheckResult result)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
            key?.SetValue(LastCheckValueName, result.CheckedAt.ToString("O"));
            key?.SetValue(LastSucceededValueName, result.Succeeded ? 1 : 0, RegistryValueKind.DWord);
            key?.SetValue(UpdateAvailableValueName, result.UpdateAvailable ? 1 : 0, RegistryValueKind.DWord);
            key?.SetValue(IsInstalledValueName, result.IsInstalled ? 1 : 0, RegistryValueKind.DWord);
            key?.SetValue(AvailableVersionValueName, result.AvailableVersion ?? string.Empty);
        }
        catch (Exception ex)
        {
            Trace.Error("updates", $"last-check write failed type={ex.GetType().Name}");
        }
    }
}
