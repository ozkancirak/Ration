using Microsoft.Win32;
using Ration.Core.Diagnostics;

namespace Ration.Platform.Windows.App;

/// <summary>
/// Removes everything Ration wrote to the user's registry on uninstall: the startup entry,
/// the notification identity and the settings key. Does not touch files (settings, log).
/// </summary>
public static class UninstallCleanup
{
    public const string SettingsKeyPath = @"Software\Ration";
    public const string NotificationKeyPath = @"Software\Classes\AppUserModelId\Ration.QuotaTray";

    public static void Run()
    {
        Try(() =>
        {
            using var run = Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKeyPath, writable: true);
            run?.DeleteValue(StartupRegistration.ValueName, throwOnMissingValue: false);
        });
        Try(() => Registry.CurrentUser.DeleteSubKeyTree(NotificationKeyPath, throwOnMissingSubKey: false));
        Try(() => Registry.CurrentUser.DeleteSubKeyTree(SettingsKeyPath, throwOnMissingSubKey: false));
    }

    private static void Try(Action step)
    {
        try { step(); }
        catch (Exception ex) { Trace.Error("uninstall", $"cleanup type={ex.GetType().Name}"); }
    }
}
