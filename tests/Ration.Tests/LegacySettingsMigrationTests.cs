using Ration.Core;

namespace Ration.Tests;

public sealed class LegacySettingsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ration-migration-{Guid.NewGuid():N}");

    [Fact]
    public void MovesOnlyPreferencesAndDoesNotOverwriteAnExistingInstallation()
    {
        var oldRoot = Path.Combine(_root, LegacySettingsMigration.LegacyProductName);
        var newRoot = Path.Combine(_root, "Ration");
        Directory.CreateDirectory(Path.Combine(oldRoot, "snapshots"));
        foreach (var name in new[] { "theme.json", "refresh.json", "settings.json", "pricing.json", "auth.json" })
            File.WriteAllText(Path.Combine(oldRoot, name), "{}");
        File.WriteAllText(Path.Combine(oldRoot, "snapshots", "claude.json"), "{}");

        LegacySettingsMigration.Run(_root);

        foreach (var name in new[] { "theme.json", "refresh.json", "settings.json" })
        {
            Assert.Equal("{}", File.ReadAllText(Path.Combine(newRoot, name)));
            Assert.False(File.Exists(Path.Combine(oldRoot, name)));
        }
        Assert.False(File.Exists(Path.Combine(newRoot, "pricing.json")));
        Assert.False(File.Exists(Path.Combine(newRoot, "auth.json")));
        Assert.False(Directory.Exists(Path.Combine(newRoot, "snapshots")));
        Assert.True(File.Exists(Path.Combine(oldRoot, "pricing.json")));
        Assert.True(File.Exists(Path.Combine(oldRoot, "snapshots", "claude.json")));

        File.WriteAllText(Path.Combine(oldRoot, "theme.json"), "old");
        File.WriteAllText(Path.Combine(newRoot, "theme.json"), "new");
        LegacySettingsMigration.Run(_root);
        Assert.Equal("new", File.ReadAllText(Path.Combine(newRoot, "theme.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(oldRoot, "theme.json")));
    }

    [Fact]
    public void FreshInstallationCreatesNoLegacyData()
    {
        LegacySettingsMigration.Run(_root);
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
