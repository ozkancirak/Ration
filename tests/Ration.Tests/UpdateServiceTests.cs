using Ration.Core.Updates;

namespace Ration.Tests;

public class UpdateServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NotInstalled_ReportsNotInstalledWithoutAskingTheSource()
    {
        var source = new FakeSource { Installed = false };
        var service = new UpdateService(source, new MemoryStore(), () => Now);

        var result = await service.CheckAsync();

        Assert.False(result.IsInstalled);
        Assert.False(result.Succeeded);
        Assert.False(result.UpdateAvailable);
        Assert.Equal(0, source.CheckCalls);
    }

    [Fact]
    public async Task NoNewVersion_IsUpToDate()
    {
        var service = new UpdateService(new FakeSource(), new MemoryStore(), () => Now);

        var result = await service.CheckAsync();

        Assert.True(result.IsInstalled);
        Assert.True(result.Succeeded);
        Assert.False(result.UpdateAvailable);
        Assert.Null(result.AvailableVersion);
        Assert.Equal(Now, result.CheckedAt);
    }

    [Fact]
    public async Task NewVersion_IsReported()
    {
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0") };
        var service = new UpdateService(source, new MemoryStore(), () => Now);

        var result = await service.CheckAsync();

        Assert.True(result.UpdateAvailable);
        Assert.Equal("0.3.0", result.AvailableVersion);
    }

    [Fact]
    public async Task SourceFailure_IsReportedAsFailedCheck()
    {
        var source = new FakeSource { Failure = new HttpRequestException("offline") };
        var service = new UpdateService(source, new MemoryStore(), () => Now);

        var result = await service.CheckAsync();

        Assert.True(result.IsInstalled);
        Assert.False(result.Succeeded);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        var source = new FakeSource { Failure = new OperationCanceledException() };
        var service = new UpdateService(source, new MemoryStore(), () => Now);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task Result_IsStoredAndReadBack()
    {
        var store = new MemoryStore();
        var service = new UpdateService(new FakeSource { Next = new AvailableUpdate("0.3.0") }, store, () => Now);

        var result = await service.CheckAsync();

        Assert.Equal(result, store.Saved);
        Assert.Equal(result, service.LastResult);
    }

    [Fact]
    public async Task StoreFailure_DoesNotBreakTheCheck()
    {
        var service = new UpdateService(new FakeSource(), new MemoryStore { FailSave = true }, () => Now);

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Download_AfterCheck_DownloadsTheFoundUpdate()
    {
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0") };
        var service = new UpdateService(source, new MemoryStore(), () => Now);
        await service.CheckAsync();

        var downloaded = await service.DownloadAsync();

        Assert.True(downloaded);
        Assert.True(service.IsDownloaded);
        Assert.Equal(1, source.DownloadCalls);
        Assert.Equal(1, source.CheckCalls);
    }

    [Fact]
    public async Task Download_WithoutPriorCheck_FindsTheUpdateFirst()
    {
        // Önceki oturumdan "güncelleme var" bilgisi kalmış olabilir; kaynak nesnesi bellekte yoktur.
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0") };
        var service = new UpdateService(source, new MemoryStore(), () => Now);

        Assert.True(await service.DownloadAsync());
        Assert.Equal(1, source.CheckCalls);
        Assert.Equal(1, source.DownloadCalls);
    }

    [Fact]
    public async Task Download_WhenNothingIsNew_DoesNotDownload()
    {
        var source = new FakeSource();
        var service = new UpdateService(source, new MemoryStore(), () => Now);

        Assert.False(await service.DownloadAsync());
        Assert.False(service.IsDownloaded);
        Assert.Equal(0, source.DownloadCalls);
    }

    [Fact]
    public async Task Download_Failure_ReturnsFalseAndLeavesTheServiceUsable()
    {
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0"), DownloadFailure = new IOException("disk full") };
        var service = new UpdateService(source, new MemoryStore(), () => Now);
        await service.CheckAsync();

        Assert.False(await service.DownloadAsync());
        Assert.False(service.IsDownloaded);

        source.DownloadFailure = null;
        Assert.True(await service.DownloadAsync());
    }

    [Fact]
    public async Task NewerVersionFound_ClearsTheDownloadedFlag()
    {
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0") };
        var service = new UpdateService(source, new MemoryStore(), () => Now);
        await service.CheckAsync();
        await service.DownloadAsync();

        source.Next = new AvailableUpdate("0.3.1");
        await service.CheckAsync();

        Assert.False(service.IsDownloaded);
    }

    [Theory]
    [InlineData("0.3.0", "0.2.3", true)]
    [InlineData("0.2.3", "0.2.3", false)]
    [InlineData("0.2.2", "0.2.3", false)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.3.0", "0.3.0-beta.1", true)]
    [InlineData("0.3.0-beta.2", "0.3.0", false)]
    [InlineData("v0.3.0", "0.2.3", true)]
    [InlineData(null, "0.2.3", false)]
    [InlineData("junk", "0.2.3", false)]
    public void IsNewer_ComparesVersions(string? available, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(available, current));
    }

    private sealed class FakeSource : IUpdateSource
    {
        public bool Installed { get; set; } = true;
        public AvailableUpdate? Next { get; set; }
        public Exception? Failure { get; set; }
        public int CheckCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public Exception? DownloadFailure { get; set; }

        public bool IsInstalled => Installed;

        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Next);
        }

        public Task DownloadAsync(AvailableUpdate update, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            if (DownloadFailure is not null) throw DownloadFailure;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryStore : IUpdateStateStore
    {
        public UpdateCheckResult? Saved { get; private set; }
        public bool FailSave { get; set; }

        public UpdateCheckResult? Load() => Saved;

        public void Save(UpdateCheckResult result)
        {
            if (FailSave) throw new IOException("disk");
            Saved = result;
        }
    }
}
