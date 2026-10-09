using Microsoft.Win32;
using Ration.Core.Settings;

namespace Ration.Platform.Windows.App;

/// <summary>Eski sürümlerin HKCU\Software\Ration altındaki değerleri.</summary>
public sealed class LegacyRegistryValues : ILegacyRegistryValues
{
    public object? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallCleanup.SettingsKeyPath, writable: false);
        return key?.GetValue(name);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallCleanup.SettingsKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
