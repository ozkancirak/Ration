namespace Ration.Core.Refresh;

/// <summary>
/// Sağlayıcı başına üstel geri çekilme.
///
/// Bir sağlayıcı arka arkaya hata veriyorsa (ağ yok, token ölmüş, uç nokta 500)
/// her turda tekrar denemek ne kullanıcıya fayda sağlar ne de sağlayıcıya.
/// Başarısızlık sayısı arttıkça bekleme süresi ikiye katlanır, bir tavanda durur.
/// İlk başarıda sıfırlanır.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;

    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public CircuitBreaker(TimeSpan? initialBackoff = null, TimeSpan? maxBackoff = null)
    {
        _initialBackoff = initialBackoff ?? TimeSpan.FromSeconds(30);
        _maxBackoff = maxBackoff ?? TimeSpan.FromMinutes(15);
    }

    public int ConsecutiveFailures { get; private set; }

    public TimeSpan CurrentBackoff { get; private set; } = TimeSpan.Zero;

    /// <summary>Devre açıksa bu sağlayıcı bu turda atlanır.</summary>
    public bool IsOpen(DateTimeOffset now) => now < _openUntil;

    public TimeSpan RemainingCooldown(DateTimeOffset now) =>
        _openUntil > now ? _openUntil - now : TimeSpan.Zero;

    public void RecordSuccess()
    {
        ConsecutiveFailures = 0;
        CurrentBackoff = TimeSpan.Zero;
        _openUntil = DateTimeOffset.MinValue;
    }

    public void RecordFailure(DateTimeOffset now)
    {
        ConsecutiveFailures++;

        // 30s, 60s, 120s, ... tavana kadar. Üs kaydırmayla hesaplanır;
        // uzun kesintilerde taşmaması için önce tavanla karşılaştırılır.
        var multiplier = Math.Min(ConsecutiveFailures - 1, 20);
        var ticks = _initialBackoff.Ticks * (1L << multiplier);

        CurrentBackoff = ticks <= 0 || ticks > _maxBackoff.Ticks
            ? _maxBackoff
            : TimeSpan.FromTicks(ticks);

        _openUntil = now + CurrentBackoff;
    }
}
