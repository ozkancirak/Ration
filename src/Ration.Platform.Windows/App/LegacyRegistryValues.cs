using Microsoft.Win32;
using Ration.Core.Settings;

namespace Ration.Platform.Windows.App;

/// <summary>Values of older versions under HKCU\Software\Ration.</summary>
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
