using System.Collections.Concurrent;
using System.Diagnostics;
using Kalan.Core.Abstractions;
using Kalan.Core.Diagnostics;
using Kalan.Core.Model;
using Kalan.Core.Providers;
using KalanTrace = Kalan.Core.Diagnostics.Trace;

namespace Kalan.Core.Refresh;

public sealed record RefreshOptions
{
    /// <summary>Turlar arası bekleme. Manuel mod için TimeSpan.Zero verilmez; Start çağrılmaz.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Aynı anda kaç sağlayıcı sorgulanabilir.</summary>
    public int MaxConcurrency { get; init; } = 3;

    /// <summary>Sağlayıcı başına rastgele gecikme; hepsi aynı saniyede ateşlemesin.</summary>
    public TimeSpan MaxJitter { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Açılışta diskteki son veriyi hemen yayınla (boş ekran gösterme).</summary>
    public bool EmitCachedOnStart { get; init; } = true;
}

/// <summary>
/// Sağlayıcıları periyodik olarak yeniler.
///
/// UI'dan tamamen bağımsızdır: olayları düz .NET event'i olarak yayar, çağıran taraf
/// kendi thread'ine marshal eder (WinUI'da DispatcherQueue.TryEnqueue).
///
/// Davranış kuralları:
/// - Sonuç Ok/Degraded ise cache'e yazılır ve yayınlanır.
/// - Hata ise devre kesici tetiklenir ve mümkünse SON İYİ VERİ bayatlık notuyla
///   yayınlanır — boş kutu göstermek, eski veri göstermekten kötüdür.
/// - Devre açıkken sağlayıcı hiç sorgulanmaz.
/// </summary>
public sealed class RefreshScheduler : IAsyncDisposable
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly SnapshotCache _cache;
    private readonly RefreshOptions _options;
    private readonly SemaphoreSlim _gate;
    private readonly Dictionary<string, CircuitBreaker> _breakers;
    private readonly ConcurrentDictionary<string, UsageSnapshot> _current = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private long _intervalTicks;

    private Task? _loop;
    private volatile bool _paused;

    public event Action<UsageSnapshot>? SnapshotUpdated;

    public RefreshScheduler(
        IEnumerable<IUsageProvider> providers,
        SnapshotCache? cache = null,
        RefreshOptions? options = null)
    {
        _providers = providers.ToList();
        _cache = cache ?? new SnapshotCache();
        _options = options ?? new RefreshOptions();
        _intervalTicks = NormalizeInterval(_options.Interval).Ticks;
        _gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));

        _breakers = _providers.ToDictionary(
            p => p.Id,
            _ => new CircuitBreaker(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Bilinen son snapshot'lar. UI ilk çizimini buradan yapabilir.</summary>
    public IReadOnlyDictionary<string, UsageSnapshot> Current => _current;

    public TimeSpan Interval => TimeSpan.FromTicks(Volatile.Read(ref _intervalTicks));

    /// <summary>Canlı ayar değişikliğini sonraki arka plan turuna uygular.</summary>
    public void SetInterval(TimeSpan interval) =>
        Volatile.Write(ref _intervalTicks, NormalizeInterval(interval).Ticks);

    /// <summary>Ekran kilitli ya da pil tasarrufundayken çağrılır.</summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public void Start()
    {
        if (_loop is not null) return;

        if (_options.EmitCachedOnStart) EmitCachedSnapshots();

        _loop = RunAsync(_stopping.Token);
    }

    private void EmitCachedSnapshots()
    {
        foreach (var provider in _providers)
        {
            var cached = _cache.TryLoad(provider.Id);
            if (cached is null) continue;

            Publish(MarkStale(cached, "Önceki oturumdan"));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RefreshAllAsync(ct).ConfigureAwait(false);

            while (true)
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);
                if (_paused) continue;

                await RefreshAllAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);

        var tasks = _providers.Select(p => RefreshOneAsync(p, linked.Token));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RefreshOneAsync(IUsageProvider provider, CancellationToken ct)
    {
        var breaker = _breakers[provider.Id];
        var now = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        KalanTrace.Info("provider.refresh", $"start provider={provider.Id}");

        if (breaker.IsOpen(now))
        {
            var remaining = (int)Math.Ceiling(breaker.RemainingCooldown(now).TotalSeconds);
            KalanTrace.Info(
                "provider.refresh",
                $"result provider={provider.Id} source=none status=skipped durationMs={stopwatch.ElapsedMilliseconds}");
            PublishFallback(provider.Id, $"Sağlayıcı geçici olarak atlanıyor ({remaining} sn sonra tekrar denenecek)");
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Jitter: tüm sağlayıcılar aynı saniyede ateşlerse hem ağ hem UI dalgalanır.
            if (_options.MaxJitter > TimeSpan.Zero)
            {
                var jitterMs = Random.Shared.Next(0, (int)_options.MaxJitter.TotalMilliseconds + 1);
                await Task.Delay(jitterMs, ct).ConfigureAwait(false);
            }

            var snapshot = await ProviderResolver.ResolveAsync(provider, ct).ConfigureAwait(false);
            KalanTrace.Info(
                "provider.refresh",
                $"result provider={provider.Id} source={snapshot.ResolvedVia?.ToString() ?? "none"} status={snapshot.Status} durationMs={stopwatch.ElapsedMilliseconds}");

            if (snapshot.Status is ProviderStatus.Ok or ProviderStatus.Degraded)
            {
                breaker.RecordSuccess();
                _cache.Save(snapshot);
                Publish(snapshot);
                return;
            }

            breaker.RecordFailure(DateTimeOffset.UtcNow);

            // Son iyi veriyi bayat notuyla göster; yoksa hatanın kendisini göster.
            var cached = _cache.TryLoad(provider.Id);
            Publish(cached is null ? snapshot : MarkStale(cached, snapshot.StaleReason));
        }
        catch (OperationCanceledException)
        {
            KalanTrace.Info(
                "provider.refresh",
                $"result provider={provider.Id} source=none status=cancelled durationMs={stopwatch.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex)
        {
            breaker.RecordFailure(DateTimeOffset.UtcNow);
            KalanTrace.Info(
                "provider.refresh",
                $"result provider={provider.Id} source=none status=exception:{ex.GetType().Name} durationMs={stopwatch.ElapsedMilliseconds}");
            PublishFallback(provider.Id, $"Beklenmedik hata: {ex.GetType().Name}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PublishFallback(string providerId, string reason)
    {
        var cached = _cache.TryLoad(providerId);

        Publish(cached is null
            ? Snapshot.Empty(providerId, ProviderStatus.Error, reason)
            : MarkStale(cached, reason));
    }

    private static UsageSnapshot MarkStale(UsageSnapshot cached, string? reason)
    {
        var age = DateTimeOffset.UtcNow - cached.FetchedAt;

        var ageText = age.TotalMinutes switch
        {
            < 1 => "az önce",
            < 60 => $"{(int)age.TotalMinutes} dk önce",
            < 1440 => $"{(int)age.TotalHours} sa önce",
            _ => $"{(int)age.TotalDays} gün önce",
        };

        var prefix = string.IsNullOrWhiteSpace(reason) ? "Yenilenemedi" : reason;

        return cached with
        {
            Status = ProviderStatus.Degraded,
            StaleReason = $"{prefix} — gösterilen veri {ageText} alındı",
        };
    }

    private void Publish(UsageSnapshot snapshot)
    {
        _current[snapshot.ProviderId] = snapshot;
        SnapshotUpdated?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopping.IsCancellationRequested) await _stopping.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _stopping.Dispose();
        _gate.Dispose();
    }

    private static TimeSpan NormalizeInterval(TimeSpan interval) =>
        interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(5);
}
