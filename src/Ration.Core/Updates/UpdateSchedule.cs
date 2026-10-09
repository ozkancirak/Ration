namespace Ration.Core.Updates;

/// <summary>Otomatik güncelleme denetiminin ne zaman yapılacağı.</summary>
public static class UpdateSchedule
{
    /// <summary>Başarılı denetimden sonra bir sonrakine kadar.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Başarısız denetimden sonra; çevrimdışı açılışta günlerce beklenmez.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(2);

    public static bool IsDue(UpdateCheckResult? last, DateTimeOffset now)
    {
        if (last is null) return true;

        var elapsed = now - last.CheckedAt;
        // Saat geri alındıysa (negatif süre) kayıt güvenilmezdir; yeniden denetle.
        if (elapsed < TimeSpan.Zero) return true;
        return elapsed >= (last.Succeeded ? Interval : RetryInterval);
    }
}
