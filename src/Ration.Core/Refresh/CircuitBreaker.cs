namespace Ration.Core.Refresh;

/// <summary>
/// Per-provider exponential backoff.
///
/// If a provider fails repeatedly (no network, dead token, endpoint returning 500),
/// retrying on every round helps neither the user nor the provider.
/// The wait doubles as the failure count grows and stops at a ceiling.
/// It resets on the first success.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;

    // A background round and a manual refresh can record a result at the same time.
    private readonly object _gate = new();
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public CircuitBreaker(TimeSpan? initialBackoff = null, TimeSpan? maxBackoff = null)
    {
        _initialBackoff = initialBackoff ?? TimeSpan.FromSeconds(30);
        _maxBackoff = maxBackoff ?? TimeSpan.FromMinutes(15);
    }

    public int ConsecutiveFailures { get; private set; }

    public TimeSpan CurrentBackoff { get; private set; } = TimeSpan.Zero;

    /// <summary>If the circuit is open, this provider is skipped in this round.</summary>
    public bool IsOpen(DateTimeOffset now)
    {
        lock (_gate) return now < _openUntil;
    }

    public TimeSpan RemainingCooldown(DateTimeOffset now)
    {
        lock (_gate) return _openUntil > now ? _openUntil - now : TimeSpan.Zero;
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            ConsecutiveFailures = 0;
            CurrentBackoff = TimeSpan.Zero;
            _openUntil = DateTimeOffset.MinValue;
        }
    }

    public void RecordFailure(DateTimeOffset now)
    {
        lock (_gate)
        {
            ConsecutiveFailures++;

            // 30s, 60s, 120s, ... up to the ceiling. Computed by bit shifting;
            // it is compared with the ceiling first so that it cannot overflow on long outages.
            var multiplier = Math.Min(ConsecutiveFailures - 1, 20);
            var ticks = _initialBackoff.Ticks * (1L << multiplier);

            CurrentBackoff = ticks <= 0 || ticks > _maxBackoff.Ticks
                ? _maxBackoff
                : TimeSpan.FromTicks(ticks);

            _openUntil = now + CurrentBackoff;
        }
    }
}
