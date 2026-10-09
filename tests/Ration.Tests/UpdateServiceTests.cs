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

    private sealed class FakeSource : IUpdateSource
    {
        public bool Installed { get; set; } = true;
        public AvailableUpdate? Next { get; set; }
        public Exception? Failure { get; set; }
        public int CheckCalls { get; private set; }

        public bool IsInstalled => Installed;

        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Next);
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
