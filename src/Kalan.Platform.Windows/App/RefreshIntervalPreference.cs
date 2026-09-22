using System.Text.Json;
using Kalan.Core.Diagnostics;

namespace Kalan.Platform.Windows.App;

/// <summary>Arka plan yenileme aralığını Kalan'ın kendi ayar alanında tutar.</summary>
public static class RefreshIntervalPreference
{
    private static readonly TimeSpan[] Allowed =
    {
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
    };

    private static readonly object Gate = new();
    private static TimeSpan? _current;

    public static event Action<TimeSpan>? Changed;

    public static TimeSpan Current
    {
        get
        {
            lock (Gate) return _current ??= Load();
        }
    }

    public static void Set(TimeSpan interval)
    {
        var normalized = Allowed.Contains(interval) ? interval : Allowed[0];

        lock (Gate)
        {
            if (Current == normalized) return;
            _current = normalized;
            Save(normalized);
        }

        Changed?.Invoke(normalized);
    }

    public static TimeSpan FromMinutes(int minutes) =>
        Allowed.FirstOrDefault(value => value.TotalMinutes == minutes, Allowed[0]);

    public static int ToMinutes(TimeSpan interval) => (int)interval.TotalMinutes;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kalan",
        "refresh.json");

    private static TimeSpan Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return Allowed[0];

            using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("minutes", out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var minutes))
            {
                return FromMinutes(minutes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.Error("settings", $"refresh-read failed type={ex.GetType().Name}");
        }

        return Allowed[0];
    }

    private static void Save(TimeSpan interval)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { minutes = ToMinutes(interval) }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.Error("settings", $"refresh-write failed type={ex.GetType().Name}");
        }
    }
}
