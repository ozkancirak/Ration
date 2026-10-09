using Ration.Core.Settings;

namespace Ration.Tests;

public sealed class LegacyPreferenceImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ration-import-{Guid.NewGuid():N}");

    public LegacyPreferenceImportTests() => Directory.CreateDirectory(_dir);

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private SettingsStore Store() => new(_dir);

    [Fact]
    public void LanguageFile_IsImportedAndRemoved()
    {
        var path = Write("language.json", """{"language":"tr"}""");
        var store = Store();

        Assert.Equal(1, LegacyPreferenceImport.Run(store, _dir));

        Assert.Equal("tr", store.GetString("language"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ThemeFile_IsImported()
    {
        Write("theme.json", """{"theme":"dark"}""");
        var store = Store();

        LegacyPreferenceImport.Run(store, _dir);

        Assert.Equal("dark", store.GetString("theme"));
    }

    [Fact]
    public void RefreshFile_IsImported()
    {
        Write("refresh.json", """{"minutes":30}""");
        var store = Store();

        LegacyPreferenceImport.Run(store, _dir);

        Assert.Equal(30, store.GetInt("refreshMinutes", 15));
    }

    [Fact]
    public void TrayFile_IsImported()
    {
        Write("tray.json", """{"provider":"codex"}""");
        var store = Store();

        LegacyPreferenceImport.Run(store, _dir);

        Assert.Equal("codex", store.GetString("trayProvider"));
    }

    [Fact]
    public void NotificationsFile_ImportsTheSwitchAndTheFiredKeys()
    {
        Write("notifications.json", """{"enabled":false,"fired":["a","b"]}""");
        var store = Store();

        Assert.Equal(2, LegacyPreferenceImport.Run(store, _dir));

        Assert.False(store.GetBool("notificationsEnabled", true));
        Assert.Equal(["a", "b"], store.GetStrings("firedAlerts"));
    }

    [Fact]
    public void Registry_ImportsTheLastUpdateCheckAndClearsTheValues()
    {
        var registry = new FakeRegistry
        {
            ["UpdatesLastCheckedUtc"] = "2026-10-08T09:30:00.0000000+00:00",
            ["UpdatesLastSucceeded"] = 1,
            ["UpdatesUpdateAvailable"] = 1,
            ["UpdatesIsInstalled"] = 1,
            ["UpdatesAvailableVersion"] = "0.3.0",
        };
        var store = Store();

        Assert.Equal(1, LegacyPreferenceImport.Run(store, _dir, registry));

        Assert.Equal("2026-10-08T09:30:00.0000000+00:00", store.GetString("updateCheckedAt"));
        Assert.True(store.GetBool("updateCheckSucceeded", false));
        Assert.True(store.GetBool("updateAvailable", false));
        Assert.True(store.GetBool("updateInstalled", false));
        Assert.Equal("0.3.0", store.GetString("updateVersion"));
        Assert.Empty(registry.Remaining);
    }

    [Fact]
    public void Registry_WithoutAValidTimestamp_ImportsNothingButStillCleansUp()
    {
        var registry = new FakeRegistry { ["UpdatesLastCheckedUtc"] = "garbage", ["UpdatesIsInstalled"] = 1 };
        var store = Store();

        Assert.Equal(0, LegacyPreferenceImport.Run(store, _dir, registry));

        Assert.False(store.Has("updateCheckedAt"));
        Assert.Empty(registry.Remaining);
    }

    [Fact]
    public void ExistingValues_AreNeverOverwritten()
    {
        var store = Store();
        store.Set("language", "en");
        store.Set("refreshMinutes", 5);
        Write("language.json", """{"language":"tr"}""");
        Write("refresh.json", """{"minutes":60}""");

        Assert.Equal(0, LegacyPreferenceImport.Run(store, _dir));

        Assert.Equal("en", store.GetString("language"));
        Assert.Equal(5, store.GetInt("refreshMinutes", 15));
        Assert.False(File.Exists(Path.Combine(_dir, "language.json")));
    }

    [Theory]
    [InlineData("language.json", """{"language":"klingon"}""")]
    [InlineData("theme.json", """{"theme":"purple"}""")]
    [InlineData("refresh.json", """{"minutes":7}""")]
    [InlineData("refresh.json", """{"minutes":"soon"}""")]
    [InlineData("tray.json", """{"provider":""}""")]
    [InlineData("notifications.json", """{"enabled":"yes"}""")]
    public void InvalidValues_AreIgnored(string file, string json)
    {
        Write(file, json);
        var store = Store();

        Assert.Equal(0, LegacyPreferenceImport.Run(store, _dir));
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void CorruptFile_DoesNotBlockTheOthers()
    {
        Write("language.json", "{ not json");
        Write("theme.json", """{"theme":"light"}""");
        var store = Store();

        Assert.Equal(1, LegacyPreferenceImport.Run(store, _dir));

        Assert.Equal("light", store.GetString("theme"));
        Assert.False(File.Exists(Path.Combine(_dir, "language.json")));
    }

    [Fact]
    public void FiredKeys_AreCappedAtTheNewestTwoHundred()
    {
        var keys = string.Join(",", Enumerable.Range(0, 250).Select(i => $"\"k{i}\""));
        Write("notifications.json", $$"""{"fired":[{{keys}}]}""");
        var store = Store();

        LegacyPreferenceImport.Run(store, _dir);

        var fired = store.GetStrings("firedAlerts");
        Assert.Equal(200, fired.Count);
        Assert.Equal("k50", fired[0]);
        Assert.Equal("k249", fired[^1]);
    }

    [Fact]
    public void SecondRun_FindsNothingToDo()
    {
        Write("language.json", """{"language":"tr"}""");
        var store = Store();
        LegacyPreferenceImport.Run(store, _dir);

        Assert.Equal(0, LegacyPreferenceImport.Run(store, _dir));
        Assert.Equal("tr", store.GetString("language"));
    }

    [Fact]
    public void FreshInstall_CreatesNoFiles()
    {
        Directory.Delete(_dir);
        var store = Store();

        Assert.Equal(0, LegacyPreferenceImport.Run(store, _dir, new FakeRegistry()));

        Assert.False(Directory.Exists(_dir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeRegistry : ILegacyRegistryValues
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public object? this[string name]
        {
            set => _values[name] = value;
        }

        public IReadOnlyCollection<string> Remaining => _values.Keys;

        public object? Read(string name) => _values.GetValueOrDefault(name);

        public void Delete(string name) => _values.Remove(name);
    }
}
