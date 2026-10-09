using Ration.Core.Settings;

namespace Ration.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ration-settings-{Guid.NewGuid():N}");

    [Fact]
    public void MissingFile_GivesFallbacks()
    {
        var store = new SettingsStore(_dir);

        Assert.Equal("x", store.GetString("a", "x"));
        Assert.Equal(7, store.GetInt("b", 7));
        Assert.True(store.GetBool("c", true));
        Assert.Empty(store.GetStrings("d"));
        Assert.False(store.Has("a"));
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Values_SurviveANewInstance()
    {
        var store = new SettingsStore(_dir);
        store.Set("language", "tr");
        store.Set("refreshMinutes", 30);
        store.Set("notificationsEnabled", false);
        store.Set("firedAlerts", ["claude:session", "codex:weekly"]);

        var reopened = new SettingsStore(_dir);

        Assert.Equal("tr", reopened.GetString("language"));
        Assert.Equal(30, reopened.GetInt("refreshMinutes", 15));
        Assert.False(reopened.GetBool("notificationsEnabled", true));
        Assert.Equal(["claude:session", "codex:weekly"], reopened.GetStrings("firedAlerts"));
    }

    [Fact]
    public void WrongTypes_FallBackInsteadOfThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "settings.json"),
            """{"language":5,"refreshMinutes":"soon","notificationsEnabled":"yes","firedAlerts":"x"}""");
        var store = new SettingsStore(_dir);

        Assert.Null(store.GetString("language"));
        Assert.Equal(15, store.GetInt("refreshMinutes", 15));
        Assert.True(store.GetBool("notificationsEnabled", true));
        Assert.Empty(store.GetStrings("firedAlerts"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void CorruptFile_IsTreatedAsEmptyAndCanBeOverwritten(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), content);
        var store = new SettingsStore(_dir);

        Assert.False(store.Has("language"));
        store.Set("language", "en");

        Assert.Equal("en", new SettingsStore(_dir).GetString("language"));
    }

    [Fact]
    public void SettingNull_RemovesTheKey()
    {
        var store = new SettingsStore(_dir);
        store.Set("trayProvider", "claude");
        store.Set("trayProvider", (string?)null);

        Assert.False(store.Has("trayProvider"));
        Assert.False(new SettingsStore(_dir).Has("trayProvider"));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFile()
    {
        var store = new SettingsStore(_dir);
        store.Set("a", "1");
        store.Set("a", "2");

        Assert.Equal(["settings.json"], Directory.GetFiles(_dir).Select(file => Path.GetFileName(file)).ToArray());
    }

    [Fact]
    public void UnchangedValue_DoesNotRewriteTheFile()
    {
        var store = new SettingsStore(_dir);
        store.Set("a", "1");
        var written = File.GetLastWriteTimeUtc(store.Path);
        Thread.Sleep(30);

        store.Set("a", "1");

        Assert.Equal(written, File.GetLastWriteTimeUtc(store.Path));
    }

    [Fact]
    public void Clear_DeletesTheFileAndForgetsValues()
    {
        var store = new SettingsStore(_dir);
        store.Set("language", "tr");

        store.Clear();

        Assert.False(File.Exists(store.Path));
        Assert.False(store.Has("language"));
        Assert.Equal("en", store.GetString("language", "en"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
