using System.Text.Json;
using Kalan.Core.Diagnostics;

namespace Kalan.Platform.Windows.Theme;

public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Kalan'ın kendi tema seçimi. UI katmanı bunu FrameworkElement.RequestedTheme'e
/// uygular; Application.RequestedTheme kullanılmaz çünkü pencereler oluşturulduktan
/// sonra canlı olarak değiştirilemez.
/// </summary>
public static class AppThemePreference
{
    private const string ThemeProperty = "theme";
    private static readonly object Gate = new();
    private static AppThemeMode? _current;

    public static event Action<AppThemeMode>? Changed;

    public static AppThemeMode Current
    {
        get
        {
            lock (Gate)
            {
                return _current ??= Load();
            }
        }
    }

    public static void Set(AppThemeMode mode)
    {
        lock (Gate)
        {
            if (Current == mode) return;
            _current = mode;
            Save(mode);
        }

        Changed?.Invoke(mode);
    }

    public static bool IsAppLightTheme() => Current switch
    {
        AppThemeMode.Light => true,
        AppThemeMode.Dark => false,
        _ => WindowsThemeListener.IsAppLightTheme(),
    };

    public static bool IsTaskbarLightTheme() => Current switch
    {
        AppThemeMode.Light => true,
        AppThemeMode.Dark => false,
        _ => WindowsThemeListener.IsTaskbarLightTheme(),
    };

    public static string ToTag(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => "light",
        AppThemeMode.Dark => "dark",
        _ => "system",
    };

    public static AppThemeMode FromTag(string? tag) => tag?.ToLowerInvariant() switch
    {
        "light" => AppThemeMode.Light,
        "dark" => AppThemeMode.Dark,
        _ => AppThemeMode.System,
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kalan",
        "theme.json");

    private static AppThemeMode Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return AppThemeMode.System;
            using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty(ThemeProperty, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return FromTag(value.GetString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.Error("theme", $"preference-read failed type={ex.GetType().Name}");
        }

        return AppThemeMode.System;
    }

    private static void Save(AppThemeMode mode)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new { theme = ToTag(mode) });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.Error("theme", $"preference-write failed type={ex.GetType().Name}");
        }
    }
}
