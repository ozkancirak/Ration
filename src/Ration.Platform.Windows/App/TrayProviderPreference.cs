using Ration.Core.Settings;

namespace Ration.Platform.Windows.App;

/// <summary>
/// Tepsi ikonunda gösterilecek sağlayıcı. null = otomatik (en çok kullanılan).
/// Önceden yalnızca bellekte tutuluyordu; her açılışta ve tema yeniden
/// başlatmasında "Otomatik"e dönüyordu.
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
