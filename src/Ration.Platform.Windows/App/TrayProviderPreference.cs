using Ration.Core.Settings;

namespace Ration.Platform.Windows.App;

/// <summary>
/// The provider shown in the tray icon. null = automatic (the most used).
/// It used to be kept in memory only; it went back to "Automatic" on every launch and theme restart.
/// </summary>
public static class TrayProviderPreference
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static string? _current;

    public static string? Current
    {
        get
        {
            lock (Gate)
            {
                if (!_loaded)
                {
                    _current = Load();
                    _loaded = true;
                }
                return _current;
            }
        }
    }

    public static void Set(string? providerId)
    {
        var normalized = string.IsNullOrWhiteSpace(providerId) ? null : providerId.ToLowerInvariant();
        lock (Gate)
        {
            if (Current == normalized) return;
            _current = normalized;
            Save(normalized);
        }
    }

    private const string SettingKey = "trayProvider";

    private static string? Load() => SettingsStore.Default.GetString(SettingKey);

    private static void Save(string? providerId) => SettingsStore.Default.Set(SettingKey, providerId);
}
