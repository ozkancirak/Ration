using Ration.Core.Settings;
using Ration.Core.Updates;

namespace Ration.Tests;

public sealed class SettingsUpdateStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ration-update-state-{Guid.NewGuid():N}");

    [Fact]
    public void NothingSaved_LoadsNull()
    {
        Assert.Null(new SettingsUpdateStateStore(new SettingsStore(_dir)).Load());
    }

    [Theory]
    [InlineData(true, true, "0.3.0", true)]
    [InlineData(true, false, null, true)]
    [InlineData(false, false, null, false)]
    public void Result_RoundTripsThroughANewStoreInstance(bool installed, bool available, string? version, bool succeeded)
    {
        var checkedAt = new DateTimeOffset(2026, 10, 9, 12, 30, 15, TimeSpan.Zero);
        var result = new UpdateCheckResult(installed, available, version, succeeded, checkedAt);

        new SettingsUpdateStateStore(new SettingsStore(_dir)).Save(result);
        var loaded = new SettingsUpdateStateStore(new SettingsStore(_dir)).Load();

        Assert.Equal(result, loaded);
    }

    [Fact]
    public void UnreadableTimestamp_LoadsNullInsteadOfThrowing()
    {
        var settings = new SettingsStore(_dir);
        settings.Set("updateCheckedAt", "yesterday-ish");

        Assert.Null(new SettingsUpdateStateStore(settings).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
