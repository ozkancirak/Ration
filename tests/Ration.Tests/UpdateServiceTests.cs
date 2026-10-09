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

        Assert.Equal(UpdateDownloadResult.Downloaded, downloaded);
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

        Assert.Equal(UpdateDownloadResult.Downloaded, await service.DownloadAsync());
        Assert.Equal(1, source.CheckCalls);
        Assert.Equal(1, source.DownloadCalls);
    }

    [Fact]
    public async Task Download_WhenNothingIsNew_DoesNotDownload()
    {
        var source = new FakeSource();
        var service = new UpdateService(source, new MemoryStore(), () => Now);

        Assert.Equal(UpdateDownloadResult.NothingToDownload, await service.DownloadAsync());
        Assert.False(service.IsDownloaded);
        Assert.Equal(0, source.DownloadCalls);
    }

    [Fact]
    public async Task Download_Failure_ReturnsFalseAndLeavesTheServiceUsable()
    {
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0"), DownloadFailure = new IOException("disk full") };
        var service = new UpdateService(source, new MemoryStore(), () => Now);
        await service.CheckAsync();

        Assert.Equal(UpdateDownloadResult.Failed, await service.DownloadAsync());
        Assert.False(service.IsDownloaded);

        source.DownloadFailure = null;
        Assert.Equal(UpdateDownloadResult.Downloaded, await service.DownloadAsync());
    }

    [Fact]
    public async Task Download_WhileAnotherDownloadRuns_IsBusy()
    {
        var gate = new TaskCompletionSource();
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0"), DownloadGate = gate.Task };
        var service = new UpdateService(source, new MemoryStore(), () => Now);
        await service.CheckAsync();

        var first = service.DownloadAsync();
        var second = await service.DownloadAsync();
        gate.SetResult();

        Assert.Equal(UpdateDownloadResult.Busy, second);
        Assert.Equal(UpdateDownloadResult.Downloaded, await first);
        Assert.Equal(1, source.DownloadCalls);
    }

    [Fact]
    public async Task Download_ReportsClampedProgressAndFinishesAtHundred()
    {
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0"), ReportedProgress = [-5, 0, 40, 100, 140] };
        var service = new UpdateService(source, new MemoryStore(), () => Now);
        var seen = new List<int>();

        await service.DownloadAsync(new ImmediateProgress(seen));

        Assert.Equal([0, 0, 40, 100, 100, 100], seen);
    }

    [Fact]
    public async Task Apply_BeforeDownload_DoesNothing()
    {
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0") };
        var service = new UpdateService(source, new MemoryStore(), () => Now);
        await service.CheckAsync();

        Assert.False(service.ApplyOnExitAndRestart());
        Assert.Null(source.Applied);
    }

    [Fact]
    public async Task Apply_AfterDownload_SchedulesTheDownloadedUpdate()
    {
        var update = new AvailableUpdate("0.3.0");
        var source = new FakeSource { Next = update };
        var service = new UpdateService(source, new MemoryStore(), () => Now);
        await service.CheckAsync();
        await service.DownloadAsync();

        Assert.True(service.ApplyOnExitAndRestart());
        Assert.Same(update, source.Applied);
    }

    [Fact]
    public async Task Apply_Failure_ReturnsFalseSoTheAppKeepsRunning()
    {
        var source = new FakeSource { Next = new AvailableUpdate("0.3.0"), ApplyFailure = new InvalidOperationException("locked") };
        var service = new UpdateService(source, new MemoryStore(), () => Now);
        await service.CheckAsync();
        await service.DownloadAsync();

        Assert.False(service.ApplyOnExitAndRestart());
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
    [InlineData("0.4.0-beta.2", "0.4.0-beta.1", true)]
    [InlineData("0.4.0-beta.1", "0.4.0-beta.2", false)]
    [InlineData("0.4.0-beta.1", "0.4.0-beta.1", false)]
    [InlineData("0.4.0-beta.10", "0.4.0-beta.9", true)]
    [InlineData("0.4.0-rc.1", "0.4.0-beta.9", true)]
    [InlineData("0.4.0-beta.1.1", "0.4.0-beta.1", true)]
    [InlineData("0.4.0-beta", "0.3.9", true)]
    [InlineData("v0.3.0", "0.2.3", true)]
    [InlineData(null, "0.2.3", false)]
    [InlineData("junk", "0.2.3", false)]
    public void IsNewer_ComparesVersions(string? available, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(available, current));
    }

    // Progress<T> eşzamansız gönderir; testte sıra değişmesin diye doğrudan çağıran.
    private sealed class ImmediateProgress(List<int> seen) : IProgress<int>
    {
        public void Report(int value) => seen.Add(value);
    }

    private sealed class FakeSource : IUpdateSource
    {
        public bool Installed { get; set; } = true;
        public AvailableUpdate? Next { get; set; }
        public Exception? Failure { get; set; }
        public int CheckCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public AvailableUpdate? Applied { get; private set; }
        public Exception? ApplyFailure { get; set; }
        public Task? DownloadGate { get; set; }
        public Exception? DownloadFailure { get; set; }

        public bool IsInstalled => Installed;

        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Next);
        }

        public int[] ReportedProgress { get; set; } = [];

        public void ApplyOnExitAndRestart(AvailableUpdate update)
        {
            if (ApplyFailure is not null) throw ApplyFailure;
            Applied = update;
        }

        public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            foreach (var percent in ReportedProgress) progress(percent);
            if (DownloadFailure is not null) throw DownloadFailure;
            return DownloadGate ?? Task.CompletedTask;
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
