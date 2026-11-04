using System.Text.Json.Nodes;
using Ration.Core.Settings;

namespace Ration.Tests;

public sealed class SettingsPortabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ration-portability-{Guid.NewGuid():N}");

    private SettingsStore NewStore(string name) => new(Path.Combine(_root, name));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Export_CarriesOnlyPreferences_NotInstallationState()
    {
        var store = NewStore("a");
        store.Set("language", "tr");
        store.Set("refreshMinutes", 30);
        store.Set("showUsed", true);
        store.Set("notificationsDisabledProviders", new[] { "codex" });
        store.Set("firedAlerts", new[] { "claude|x|Session|-|1|0" });
        store.Set("updateCheckedAt", "2026-09-23T12:00:00Z");
        store.Set("signInEverWorked", new[] { "claude" });
        store.Set("somethingUnknown", "x");

        var exported = JsonNode.Parse(SettingsPortability.Export(store))!.AsObject();
        var settings = exported["settings"]!.AsObject();

        Assert.Equal(1, exported["ration"]!.GetValue<int>());
        Assert.Equal(["language", "notificationsDisabledProviders", "refreshMinutes", "showUsed"], settings.Select(p => p.Key).Order());
    }

    [Fact]
    public void RoundTrip_RestoresEveryPreference()
    {
        var source = NewStore("source");
        source.Set("language", "en");
        source.Set("theme", "dark");
        source.Set("refreshMinutes", 5);
        source.Set("showUsed", true);
        source.Set("hotkey", "Ctrl+Alt+K");
        source.Set("lowQuotaThreshold", 30);
        source.Set("notificationsDisabledProviders", new[] { "codex", "cursor" });

        var target = NewStore("target");
        var result = SettingsPortability.Import(target, SettingsPortability.Export(source));

        Assert.Equal(new ImportResult(7, 0), result);
        Assert.Equal("en", target.GetString("language"));
        Assert.Equal("dark", target.GetString("theme"));
        Assert.Equal(5, target.GetInt("refreshMinutes", 0));
        Assert.True(target.GetBool("showUsed", false));
        Assert.Equal("Ctrl+Alt+K", target.GetString("hotkey"));
        Assert.Equal(30, target.GetInt("lowQuotaThreshold", 0));
        Assert.Equal(["codex", "cursor"], target.GetStrings("notificationsDisabledProviders"));
    }

    [Fact]
    public void Import_SkipsUnknownKeysAndWrongTypes_AndNeverTouchesState()
    {
        var target = NewStore("target");
        target.Set("firedAlerts", new[] { "keep" });

        const string json = """
        {
          "ration": 1,
          "settings": {
            "showUsed": true,
            "refreshMinutes": "15",
            "notificationsDisabledProviders": ["codex", 3],
            "firedAlerts": ["injected"],
            "unknown": 1
          }
        }
        """;

        var result = SettingsPortability.Import(target, json);

        Assert.Equal(new ImportResult(1, 4), result);
        Assert.True(target.GetBool("showUsed", false));
        Assert.False(target.Has("refreshMinutes"));
        Assert.False(target.Has("notificationsDisabledProviders"));
        Assert.Equal(["keep"], target.GetStrings("firedAlerts"));
        Assert.False(target.Has("unknown"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"ration\":2,\"settings\":{\"showUsed\":true}}")]
    [InlineData("{\"ration\":1}")]
    [InlineData("{\"ration\":1,\"settings\":[]}")]
    public void Import_RejectsFilesThatAreNotRationSettings_WithoutWritingAnything(string json)
    {
        var target = NewStore("target");

        Assert.Null(SettingsPortability.Import(target, json));
        Assert.False(target.Has("showUsed"));
        Assert.False(Directory.Exists(Path.Combine(_root, "target")));
    }

    [Fact]
    public void Import_RejectsOversizedFiles()
    {
        var target = NewStore("target");
        var json = "{\"ration\":1,\"settings\":{\"language\":\"" + new string('a', SettingsPortability.MaxImportBytes) + "\"}}";

        Assert.Null(SettingsPortability.Import(target, json));
    }

    [Fact]
    public void Export_OfAnEmptyStore_IsStillAValidFile()
    {
        var json = SettingsPortability.Export(NewStore("empty"));

        Assert.Equal(new ImportResult(0, 0), SettingsPortability.Import(NewStore("other"), json));
    }
}
