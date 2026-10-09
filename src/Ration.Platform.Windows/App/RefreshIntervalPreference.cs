using Ration.Core.Settings;

namespace Ration.Platform.Windows.App;

/// <summary>Arka plan yenileme aralığı; SettingsStore'da "refreshMinutes" olarak durur.</summary>
public static class RefreshIntervalPreference
{
    private static readonly TimeSpan Default = TimeSpan.FromMinutes(15);

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
        var normalized = Allowed.Contains(interval) ? interval : Default;

        lock (Gate)
        {
            if (Current == normalized) return;
            _current = normalized;
            Save(normalized);
        }

        Changed?.Invoke(normalized);
    }

    public static TimeSpan FromMinutes(int minutes) =>
        Allowed.FirstOrDefault(value => value.TotalMinutes == minutes, Default);

    public static int ToMinutes(TimeSpan interval) => (int)interval.TotalMinutes;

    private const string SettingKey = "refreshMinutes";

    private static TimeSpan Load() =>
        FromMinutes(SettingsStore.Default.GetInt(SettingKey, ToMinutes(Default)));

    private static void Save(TimeSpan interval) =>
        SettingsStore.Default.Set(SettingKey, ToMinutes(interval));
}
