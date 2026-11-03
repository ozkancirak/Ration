using System.Collections.Concurrent;
using System.Diagnostics;
using Ration.Core.Abstractions;
using Ration.Core.Diagnostics;
using Ration.Core.Model;
using Ration.Core.Providers;
using RationTrace = Ration.Core.Diagnostics.Trace;

namespace Ration.Core.Refresh;

public sealed record RefreshOptions
{
    /// <summary>Wait between rounds. TimeSpan.Zero is not passed for manual mode; Start is not called.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How many providers can be queried at the same time.</summary>
    public int MaxConcurrency { get; init; } = 3;

    /// <summary>Random delay per provider, so that they do not all fire in the same second.</summary>
    public TimeSpan MaxJitter { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>On startup, publish the last data on disk immediately (do not show an empty screen).</summary>
    public bool EmitCachedOnStart { get; init; } = true;
}

/// <summary>
/// Refreshes the providers periodically.
///
/// Completely independent of the UI: it publishes events as plain .NET events, and the caller
/// marshals to its own thread (DispatcherQueue.TryEnqueue in WinUI).
///
/// Behavior rules:
/// - If the result is Ok/Degraded it is written to the cache and published.
/// - On error the circuit breaker is triggered and, where possible, the LAST GOOD DATA is
///   published with a staleness note — showing an empty box is worse than showing old data.
/// - While the circuit is open the provider is never queried.
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

    // The in-flight refresh of the same provider. A second request (e.g. the background round at startup
    // plus opening the panel) does not send a new request, it waits for the first to finish.
    private readonly object _flightGate = new();
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Flight
    {
        // If cancelled, the jitter wait is cut short: the user joined a refresh that was already waiting.
        // It is not disposed because it has no timer or registration (a late joiner can call it safely).
        public CancellationTokenSource SkipJitter { get; } = new();

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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

    /// <summary>Last known snapshots. The UI can make its first paint from here.</summary>
    public IReadOnlyDictionary<string, UsageSnapshot> Current => _current;

    public TimeSpan Interval => TimeSpan.FromTicks(Volatile.Read(ref _intervalTicks));

    /// <summary>Applies a live setting change to the next background round.</summary>
    public void SetInterval(TimeSpan interval) =>
        Volatile.Write(ref _intervalTicks, NormalizeInterval(interval).Ticks);

    /// <summary>Called when the screen is locked or battery saver is on.</summary>
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

            Publish(MarkStale(cached, L.T("From previous session", "Önceki oturumdan")));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (!_paused)
            {
                await RefreshAllAsync(ct).ConfigureAwait(false);
            }

            while (true)
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);
                if (_paused) continue;

                await RefreshAllAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    /// <param name="skipJitter">
    /// If the user is waiting for the result (Refresh button, panel opening), the random wait is skipped;
    /// jitter exists only so that background rounds do not fire in the same second.
    /// </param>
    public async Task RefreshAllAsync(
        CancellationToken ct = default,
        bool bypassCircuitBreaker = false,
        bool skipJitter = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);

        var tasks = _providers.Select(p => RefreshOneAsync(p, linked.Token, bypassCircuitBreaker, skipJitter));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RefreshOneAsync(IUsageProvider provider, CancellationToken ct, bool bypassCircuitBreaker, bool skipJitter)
    {
        var breaker = _breakers[provider.Id];
        var now = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        RationTrace.Info("provider.refresh", $"start provider={provider.Id}");

        if (!bypassCircuitBreaker && breaker.IsOpen(now))
        {
            var remaining = (int)Math.Ceiling(breaker.RemainingCooldown(now).TotalSeconds);
            RationTrace.Info(
                "provider.refresh",
                $"result provider={provider.Id} source=none status=skipped durationMs={stopwatch.ElapsedMilliseconds}");
            PublishFallback(provider.Id, L.T($"Provider temporarily skipped (retrying in {remaining} s)", $"Sağlayıcı geçici olarak atlanıyor ({remaining} sn sonra tekrar denenecek)"));
            return;
        }

        Flight flight;
        bool owner;
        lock (_flightGate)
        {
            owner = !_flights.TryGetValue(provider.Id, out flight!);
            if (owner)
            {
                flight = new Flight();
                _flights[provider.Id] = flight;
            }
        }

        if (skipJitter) flight.SkipJitter.Cancel();

        if (!owner)
        {
            RationTrace.Info("provider.refresh", $"joined provider={provider.Id}");
            await flight.Done.Task.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await RunRefreshAsync(provider, breaker, flight, stopwatch, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_flightGate) _flights.Remove(provider.Id);
            flight.Done.TrySetResult();
        }
    }

    private async Task RunRefreshAsync(
        IUsageProvider provider,
        CircuitBreaker breaker,
        Flight flight,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Jitter: if all providers fire in the same second, both the network and the UI stutter.
            if (_options.MaxJitter > TimeSpan.Zero && !flight.SkipJitter.IsCancellationRequested)
            {
                var jitterMs = Random.Shared.Next(0, (int)_options.MaxJitter.TotalMilliseconds + 1);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, flight.SkipJitter.Token);
                try
                {
                    await Task.Delay(jitterMs, wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The user is waiting for the result: the remaining wait is skipped.
                }
            }

            var snapshot = await ProviderResolver.ResolveAsync(
                provider,
                ct,
                partial => PublishProgress(provider.Id, partial)).ConfigureAwait(false);
            snapshot = MergeProviderData(snapshot, PreviousSnapshot(provider.Id));
            RationTrace.Info(
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

            // Show the last good data with a stale note; if there is none, show the error itself.
            var cached = _cache.TryLoad(provider.Id);
            Publish(cached is null ? snapshot : MarkStale(cached, snapshot.StaleReason));
        }
        catch (OperationCanceledException)
        {
            RationTrace.Info(
                "provider.refresh",
                $"result provider={provider.Id} source=none status=cancelled durationMs={stopwatch.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex)
        {
            breaker.RecordFailure(DateTimeOffset.UtcNow);
            RationTrace.Info(
                "provider.refresh",
                $"result provider={provider.Id} source=none status=exception:{ex.GetType().Name} durationMs={stopwatch.ElapsedMilliseconds}");
            PublishFallback(provider.Id, L.T($"Unexpected error: {ex.GetType().Name}", $"Beklenmedik hata: {ex.GetType().Name}"));
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

    private void PublishProgress(string providerId, UsageSnapshot partial)
    {
        if (partial.Status is not (ProviderStatus.Ok or ProviderStatus.Degraded)) return;

        var merged = MergeProviderData(partial, PreviousSnapshot(providerId));
        _cache.Save(merged);
        Publish(merged);
    }

    private UsageSnapshot? PreviousSnapshot(string providerId) =>
        _current.TryGetValue(providerId, out var current)
            ? current
            : _cache.TryLoad(providerId);

    private static UsageSnapshot MergeProviderData(
        UsageSnapshot incoming,
        UsageSnapshot? previous)
    {
        if (previous is null ||
            !incoming.ProviderId.Equals("antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return incoming;
        }

        var windows = incoming.Windows.Count > 0 ? incoming.Windows : previous.Windows;
        var cost = incoming.Cost ?? previous.Cost;

        return incoming with
        {
            Windows = windows,
            Cost = cost,
            PlanName = incoming.PlanName ?? previous.PlanName,
            StaleReason = incoming.Windows.Count == 0 && previous.Windows.Count > 0
                ? previous.StaleReason
                : incoming.StaleReason,
        };
    }

    private static UsageSnapshot MarkStale(UsageSnapshot cached, string? reason)
    {
        var age = DateTimeOffset.UtcNow - cached.FetchedAt;

        var ageText = age.TotalMinutes switch
        {
            < 1 => L.T("just now", "az önce"),
            < 60 => L.T($"{(int)age.TotalMinutes} min ago", $"{(int)age.TotalMinutes} dk önce"),
            < 1440 => L.T($"{(int)age.TotalHours} h ago", $"{(int)age.TotalHours} sa önce"),
            _ => L.T($"{(int)age.TotalDays} d ago", $"{(int)age.TotalDays} gün önce"),
        };

        var prefix = string.IsNullOrWhiteSpace(reason) ? L.T("Could not refresh", "Yenilenemedi") : reason;

        return cached with
        {
            Status = ProviderStatus.Degraded,
            StaleReason = L.T($"{prefix} — data shown is from {ageText}", $"{prefix} — gösterilen veri {ageText} alındı"),
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
        interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(15);
}
