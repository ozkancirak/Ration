using Kalan.Core.Abstractions;
using Kalan.Core.Model;
using Kalan.Core.Refresh;

namespace Kalan.Tests;

public class CircuitBreakerTests
{
    [Fact]
    public void IlkHatadaAcilirVeBeklemeIkiyeKatlanir()
    {
        var breaker = new CircuitBreaker(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        var t0 = DateTimeOffset.UtcNow;

        Assert.False(breaker.IsOpen(t0));

        breaker.RecordFailure(t0);
        Assert.True(breaker.IsOpen(t0));
        Assert.Equal(TimeSpan.FromSeconds(1), breaker.CurrentBackoff);

        breaker.RecordFailure(t0);
        Assert.Equal(TimeSpan.FromSeconds(2), breaker.CurrentBackoff);

        breaker.RecordFailure(t0);
        Assert.Equal(TimeSpan.FromSeconds(4), breaker.CurrentBackoff);
    }

    [Fact]
    public void BeklemeTavandaDurur()
    {
        var breaker = new CircuitBreaker(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        var t0 = DateTimeOffset.UtcNow;

        for (var i = 0; i < 40; i++) breaker.RecordFailure(t0);

        // Uzun kesintide us kaydirmasi tasmamali, tavanda kalmali.
        Assert.Equal(TimeSpan.FromSeconds(10), breaker.CurrentBackoff);
    }

    [Fact]
    public void SureDolduktanSonraKapanir()
    {
        var breaker = new CircuitBreaker(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        var t0 = DateTimeOffset.UtcNow;

        breaker.RecordFailure(t0);

        Assert.True(breaker.IsOpen(t0.AddMilliseconds(500)));
        Assert.False(breaker.IsOpen(t0.AddSeconds(2)));
    }

    [Fact]
    public void BasariSifirlar()
    {
        var breaker = new CircuitBreaker(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        var t0 = DateTimeOffset.UtcNow;

        breaker.RecordFailure(t0);
        breaker.RecordFailure(t0);
        breaker.RecordSuccess();

        Assert.False(breaker.IsOpen(t0));
        Assert.Equal(0, breaker.ConsecutiveFailures);
        Assert.Equal(TimeSpan.Zero, breaker.CurrentBackoff);
    }
}

public sealed class SnapshotCacheTests : IDisposable
{
    private readonly string _dir;

    public SnapshotCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"kalan-cache-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void YazipGeriOkur()
    {
        var cache = new SnapshotCache(_dir);

        var snapshot = new UsageSnapshot(
            ProviderId: "ornek",
            Windows: new[] { new UsageWindow(WindowKind.Session, 42, 100, 42, DateTimeOffset.UtcNow, "5 saatlik") },
            Credits: null,
            Cost: null,
            Status: ProviderStatus.Ok,
            ResolvedVia: SourceKind.LocalFile,
            FetchedAt: DateTimeOffset.UtcNow,
            StaleReason: null,
            PlanName: "plus");

        cache.Save(snapshot);

        var loaded = cache.TryLoad("ornek");

        Assert.NotNull(loaded);
        Assert.Equal("ornek", loaded!.ProviderId);
        Assert.Equal("plus", loaded.PlanName);
        Assert.Equal(ProviderStatus.Ok, loaded.Status);

        var window = Assert.Single(loaded.Windows);
        Assert.Equal(42, window.Percent);
        Assert.Equal("5 saatlik", window.Label);
    }

    [Fact]
    public void OlmayanSaglayiciIcinNullDoner()
    {
        Assert.Null(new SnapshotCache(_dir).TryLoad("hic-yazilmadi"));
    }

    [Fact]
    public void SaglayiciKimliginiDosyaAdiIcinTemizler()
    {
        var cache = new SnapshotCache(_dir);

        // Yol kacisi denemesi dosya adina gecmemeli.
        cache.Save(Snapshot.Empty("../../kotu", ProviderStatus.Ok, null, SourceKind.LocalFile));

        Assert.NotNull(cache.TryLoad("../../kotu"));
        Assert.True(Directory.Exists(_dir));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void KaynakBelirsizseYazmaz()
    {
        var cache = new SnapshotCache(_dir);

        cache.Save(Snapshot.Empty("fixture", ProviderStatus.Ok, null));

        Assert.Null(cache.TryLoad("fixture"));
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void KaynagiBelirsizDosyayiOkumazVeTemizler()
    {
        var cache = new SnapshotCache(_dir);
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "fixture.json");
        File.WriteAllText(path, """
            {
              "ProviderId": "fixture",
              "Windows": [],
              "Credits": null,
              "Cost": null,
              "Status": "Ok",
              "ResolvedVia": null,
              "FetchedAt": "2026-09-21T00:00:00Z",
              "StaleReason": null,
              "PlanName": null
            }
            """);

        Assert.Null(cache.TryLoad("fixture"));
        Assert.False(File.Exists(path));
    }
}

public sealed class RefreshSchedulerTests : IDisposable
{
    private readonly string _dir;

    public RefreshSchedulerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"kalan-sched-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static RefreshOptions NoJitter => new()
    {
        MaxJitter = TimeSpan.Zero,
        EmitCachedOnStart = false,
        Interval = TimeSpan.FromHours(1),
    };

    private static UsageSnapshot Ok(string id, double percent) => new(
        ProviderId: id,
        Windows: new[] { new UsageWindow(WindowKind.Session, percent, 100, percent, null, "5 saatlik") },
        Credits: null,
        Cost: null,
        Status: ProviderStatus.Ok,
        ResolvedVia: SourceKind.LocalFile,
        FetchedAt: DateTimeOffset.UtcNow,
        StaleReason: null);

    [Fact]
    public async Task BasariliSonucYayinlanirVeCachelenir()
    {
        var cache = new SnapshotCache(_dir);
        var provider = new FakeProvider("ornek", () => Ok("ornek", 30));

        await using var scheduler = new RefreshScheduler(new[] { provider }, cache, NoJitter);

        var yayinlanan = new List<UsageSnapshot>();
        scheduler.SnapshotUpdated += s => yayinlanan.Add(s);

        await scheduler.RefreshAllAsync();

        Assert.Equal(ProviderStatus.Ok, Assert.Single(yayinlanan).Status);
        Assert.NotNull(cache.TryLoad("ornek"));
        Assert.Equal(30, scheduler.Current["ornek"].Windows[0].Percent);
    }

    [Fact]
    public async Task HataDurumundaSonIyiVeriBayatNotuylaGosterilir()
    {
        var cache = new SnapshotCache(_dir);
        cache.Save(Ok("ornek", 55) with { FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-12) });

        var provider = new FakeProvider("ornek",
            () => Snapshot.Empty("ornek", ProviderStatus.Error, "Ağ hatası"));

        await using var scheduler = new RefreshScheduler(new[] { provider }, cache, NoJitter);

        UsageSnapshot? sonuc = null;
        scheduler.SnapshotUpdated += s => sonuc = s;

        await scheduler.RefreshAllAsync();

        Assert.NotNull(sonuc);

        // Bos kutu degil: eski veri korunur, bayatligi soylenir.
        Assert.Equal(55, Assert.Single(sonuc!.Windows).Percent);
        Assert.Equal(ProviderStatus.Degraded, sonuc.Status);
        Assert.Contains("12 dk önce", sonuc.StaleReason);
        Assert.Contains("Ağ hatası", sonuc.StaleReason);
    }

    [Fact]
    public async Task CacheYokkenHataOlduguGibiYayinlanir()
    {
        var provider = new FakeProvider("ornek",
            () => Snapshot.Empty("ornek", ProviderStatus.AuthRequired, "Giriş gerekli"));

        await using var scheduler = new RefreshScheduler(new[] { provider }, new SnapshotCache(_dir), NoJitter);

        UsageSnapshot? sonuc = null;
        scheduler.SnapshotUpdated += s => sonuc = s;

        await scheduler.RefreshAllAsync();

        Assert.Equal(ProviderStatus.AuthRequired, sonuc!.Status);
        Assert.Empty(sonuc.Windows);
    }

    [Fact]
    public async Task DevreAcikkenSaglayiciTekrarSorgulanmaz()
    {
        var cagriSayisi = 0;

        var provider = new FakeProvider("ornek", () =>
        {
            cagriSayisi++;
            return Snapshot.Empty("ornek", ProviderStatus.Error, "patladı");
        });

        await using var scheduler = new RefreshScheduler(new[] { provider }, new SnapshotCache(_dir), NoJitter);

        await scheduler.RefreshAllAsync();
        await scheduler.RefreshAllAsync();
        await scheduler.RefreshAllAsync();

        // Ilk hata devreyi 30 sn acar; sonraki turlar hic sorgulamaz.
        Assert.Equal(1, cagriSayisi);
    }

    [Fact]
    public async Task ManuelTurDevreyiAtlar()
    {
        var cagriSayisi = 0;

        var provider = new FakeProvider("ornek", () =>
        {
            cagriSayisi++;
            return Snapshot.Empty("ornek", ProviderStatus.Error, "patladı");
        });

        await using var scheduler = new RefreshScheduler(new[] { provider }, new SnapshotCache(_dir), NoJitter);

        await scheduler.RefreshAllAsync();
        await scheduler.RefreshAllAsync(bypassCircuitBreaker: true);

        Assert.Equal(2, cagriSayisi);
    }

    [Fact]
    public async Task KaynakPatlarsaScheduleCokmez()
    {
        var provider = new FakeProvider("ornek", () => throw new InvalidOperationException("beklenmedik"));

        await using var scheduler = new RefreshScheduler(new[] { provider }, new SnapshotCache(_dir), NoJitter);

        UsageSnapshot? sonuc = null;
        scheduler.SnapshotUpdated += s => sonuc = s;

        await scheduler.RefreshAllAsync();

        Assert.NotNull(sonuc);
        Assert.Equal(ProviderStatus.Error, sonuc!.Status);
    }

    private sealed class FakeSource : IUsageSource
    {
        private readonly Func<UsageSnapshot> _factory;

        public FakeSource(Func<UsageSnapshot> factory) => _factory = factory;

        public SourceKind Kind => SourceKind.LocalFile;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<UsageSnapshot> FetchAsync(CancellationToken ct = default) => Task.FromResult(_factory());
    }

    private sealed class FakeProvider : IUsageProvider
    {
        public FakeProvider(string id, Func<UsageSnapshot> factory)
        {
            Id = id;
            Sources = new IUsageSource[] { new FakeSource(factory) };
        }

        public string Id { get; }

        public string DisplayName => Id;

        public ProviderCapabilities Capabilities { get; } = new(true, true, false, false, false);

        public IReadOnlyList<IUsageSource> Sources { get; }
    }
}
