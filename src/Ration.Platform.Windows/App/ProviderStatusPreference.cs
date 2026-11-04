using Ration.Core.Settings;

namespace Ration.Platform.Windows.App;

/// <summary>
/// Whether Ration looks at the providers' public status pages to explain an outage. On by default; the
/// requests carry no credentials and no account data. Stored in SettingsStore as "statusChecks".
/// </summary>
public static class ProviderStatusPreference
{
    private const string SettingKey = "statusChecks";

    public static bool Enabled => SettingsStore.Default.GetBool(SettingKey, true);

    public static void Set(bool enabled)
    {
        if (Enabled == enabled) return;
        SettingsStore.Default.Set(SettingKey, enabled);
    }
}
