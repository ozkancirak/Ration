using System.Globalization;
using Ration.Core.Settings;

namespace Ration.Core.Updates;

/// <summary>Son denetimin sonucu SettingsStore'da tutulur.</summary>
public sealed class SettingsUpdateStateStore(SettingsStore settings) : IUpdateStateStore
{
    private const string CheckedAtKey = "updateCheckedAt";
    private const string SucceededKey = "updateCheckSucceeded";
    private const string AvailableKey = "updateAvailable";
    private const string InstalledKey = "updateInstalled";
    private const string VersionKey = "updateVersion";

    public UpdateCheckResult? Load()
    {
        if (!DateTimeOffset.TryParse(
                settings.GetString(CheckedAtKey),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var checkedAt))
        {
            return null;
        }

        return new UpdateCheckResult(
            settings.GetBool(InstalledKey, false),
            settings.GetBool(AvailableKey, false),
            settings.GetString(VersionKey),
            settings.GetBool(SucceededKey, false),
            checkedAt);
    }

    public void Save(UpdateCheckResult result)
    {
        settings.Set(CheckedAtKey, result.CheckedAt.ToString("O", CultureInfo.InvariantCulture));
        settings.Set(SucceededKey, result.Succeeded);
        settings.Set(AvailableKey, result.UpdateAvailable);
        settings.Set(InstalledKey, result.IsInstalled);
        settings.Set(VersionKey, result.AvailableVersion);
    }
}
