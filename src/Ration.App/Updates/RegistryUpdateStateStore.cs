using Microsoft.Win32;
using Ration.Core.Updates;

namespace Ration.App.Updates;

/// <summary>Son denetim sonucu HKCU\Software\Ration altında tutulur.</summary>
internal sealed class RegistryUpdateStateStore : IUpdateStateStore
{
    private const string SettingsKeyPath = @"Software\Ration";
    private const string LastCheckValueName = "UpdatesLastCheckedUtc";
    private const string LastSucceededValueName = "UpdatesLastSucceeded";
    private const string UpdateAvailableValueName = "UpdatesUpdateAvailable";
    private const string AvailableVersionValueName = "UpdatesAvailableVersion";
    private const string IsInstalledValueName = "UpdatesIsInstalled";

    public UpdateCheckResult? Load()
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

    public void Save(UpdateCheckResult result)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue(LastCheckValueName, result.CheckedAt.ToString("O"));
        key.SetValue(LastSucceededValueName, result.Succeeded ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(UpdateAvailableValueName, result.UpdateAvailable ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(IsInstalledValueName, result.IsInstalled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(AvailableVersionValueName, result.AvailableVersion ?? string.Empty);
    }
}
