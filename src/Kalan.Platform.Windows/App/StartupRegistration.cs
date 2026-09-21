using Microsoft.Win32;
using Kalan.Core.Diagnostics;

namespace Kalan.Platform.Windows.App;

/// <summary>
/// Kalan'ın Velopack sabit shim'ini kullanıcı oturum başlangıcına kaydeder.
/// Durum her zaman kayıt defterinden okunur; ayar dosyasında kopyası tutulmaz.
/// </summary>
public static class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Kalan";
    public const string ShimValue = @"""%LOCALAPPDATA%\Kalan\Kalan.exe""";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(
                ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return PathsEqual(value, ShimValue);
        }
        catch (Exception ex)
        {
            Trace.Error("startup", $"read error type={ex.GetType().Name}");
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                key?.SetValue(ValueName, ShimValue, RegistryValueKind.ExpandString);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            bool actual = IsEnabled();
            Trace.Info("startup", $"set enabled={enabled} actual={actual}");
            return actual == enabled;
        }
        catch (Exception ex)
        {
            Trace.Error("startup", $"write error type={ex.GetType().Name}");
            return false;
        }
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;

        static string Normalize(string value)
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            try { return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return expanded; }
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
}
