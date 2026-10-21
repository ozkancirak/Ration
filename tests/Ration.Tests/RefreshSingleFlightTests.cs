using Ration.Core.Abstractions;
using Ration.Core.Model;
using Ration.Core.Refresh;

namespace Ration.Tests;

/// <summary>Aynı sağlayıcı için üst üste gelen yenilemeler tek istek atar.</summary>
public sealed class RefreshSingleFlightTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"ration-flight-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static UsageSnapshot Ok(string id) => new(
        ProviderId: id,
        Windows: new[] { new UsageWindow(WindowKind.Session, 30, 100, 30, null, "5 saatlik") },
        Credits: null,
        Cost: null,
        Status: ProviderStatus.Ok,
        ResolvedVia: SourceKind.LocalFile,
        FetchedAt: DateTimeOffset.UtcNow,
        StaleReason: null);

    private sealed class GatedSource(string id) : IUsageSource
    {
        private int _calls;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public SourceKind Kind => SourceKind.LocalFile;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            await Gate.Task.WaitAsync(ct);
            return Ok(id);
        }
    }

    private sealed class GatedProvider(string id, GatedSource source) : IUsageProvider
    {
        public string Id => id;

        public string DisplayName => id;

        public ProviderCapabilities Capabilities { get; } = new(true, true, false, false, false);

        public IReadOnlyList<IUsageSource> Sources { get; } = [source];
    }

    private RefreshScheduler Create(GatedSource source, TimeSpan jitter) =>
        new(
            [new GatedProvider("ornek", source)],
            new SnapshotCache(_dir),
            new RefreshOptions { MaxJitter = jitter, EmitCachedOnStart = false, Interval = TimeSpan.FromHours(1) });

    [Fact]
    public async Task EszamanliIstekler_TekFetchYapar()
    {
        var source = new GatedSource("ornek");
        await using var scheduler = Create(source, TimeSpan.Zero);

        var first = scheduler.RefreshAllAsync();
        var second = scheduler.RefreshAllAsync();
        source.Gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, source.Calls);
        Assert.Equal(ProviderStatus.Ok, scheduler.Current["ornek"].Status);
    }

    [Fact]
    public async Task BittiktenSonrakiIstek_YeniFetchYapar()
    {
        var source = new GatedSource("ornek");
        source.Gate.SetResult();
        await using var scheduler = Create(source, TimeSpan.Zero);

        await scheduler.RefreshAllAsync();
        await scheduler.RefreshAllAsync();

        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task KullaniciYenilemesiKatilinca_ArkaPlanJitterBeklemesiKisalir()
    {
        var source = new GatedSource("ornek");
        await using var scheduler = Create(source, TimeSpan.FromSeconds(60));

        var background = scheduler.RefreshAllAsync(); // jitter bekliyor olabilir
        var interactive = scheduler.RefreshAllAsync(skipJitter: true);
        source.Gate.SetResult();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(background, interactive).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"beklendi: {stopwatch.Elapsed}");
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void DevreKesici_EszamanliKayitlarSayiyiBozmaz()
    {
        var breaker = new CircuitBreaker(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;

        Parallel.For(0, 1000, _ => breaker.RecordFailure(now));

        Assert.Equal(1000, breaker.ConsecutiveFailures);
    }
}
