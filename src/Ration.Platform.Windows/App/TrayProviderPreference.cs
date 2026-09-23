using System.Text.Json;
using Ration.Core.Diagnostics;

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

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ration",
        "tray.json");

    private static string? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;

            using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("provider", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.Error("settings", $"tray-read failed type={ex.GetType().Name}");
            return null;
        }
    }

    private static void Save(string? providerId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { provider = providerId }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.Error("settings", $"tray-write failed type={ex.GetType().Name}");
        }
    }
}
