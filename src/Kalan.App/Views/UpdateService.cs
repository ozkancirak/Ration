using Velopack;
using Velopack.Sources;
using Microsoft.Win32;
using Kalan.Core.Diagnostics;

namespace Kalan.App.Views;

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
    private const string SettingsKeyPath = @"Software\Kalan";
    private const string LastCheckValueName = "UpdatesLastCheckedUtc";
    private const string DefaultRepository = "https://github.com/ozkancirak/CodexBar";

    public static string RepositoryUrl =>
        Environment.GetEnvironmentVariable("KALAN_UPDATE_REPOSITORY") is { Length: > 0 } value
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

    public static DateTimeOffset? LastCheckedAt
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
                var value = key?.GetValue(LastCheckValueName) as string;
                return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
            }
            catch { return null; }
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            var manager = new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

            if (!manager.IsInstalled)
            {
                SaveLastCheckedAt(checkedAt);
                return new(false, false, null, false, checkedAt);
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            SaveLastCheckedAt(checkedAt);

            if (update is null)
            {
                return new(true, false, null, true, checkedAt);
            }

            return new(
                true,
                true,
                update.TargetFullRelease.Version.ToString(),
                true,
                checkedAt);
        }
        catch (Exception ex)
        {
            SaveLastCheckedAt(checkedAt);
            Trace.Error("updates", $"check failed type={ex.GetType().Name}");
            return new(true, false, null, false, checkedAt);
        }
    }

    private static void SaveLastCheckedAt(DateTimeOffset timestamp)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
            key?.SetValue(LastCheckValueName, timestamp.ToString("O"));
        }
        catch (Exception ex)
        {
            Trace.Error("updates", $"last-check write failed type={ex.GetType().Name}");
        }
    }
}
