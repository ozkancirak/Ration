using Kalan.Core.Model;

namespace Kalan.Core.Usage;

/// <summary>Tempo hesabı sonucu: kota pencere bitmeden tükenir mi?</summary>
/// <param name="Tempo">kullanılan oran − geçen oran (−1..+1). Pozitif = önden gitme.</param>
/// <param name="DepletesIn">Mevcut tempoyla %100'e varmaya kalan süre tahmini.</param>
public sealed record PaceResult(double Tempo, TimeSpan DepletesIn);

/// <summary>
/// "Kotam pencere bitmeden tükenir mi?" sorusunun saf hesabı.
/// UI'da hesaplama yapılmaz; burası çağrılır, yazı <see cref="Format"/> ile alınır.
/// Gösterilmeyecek durumlarda <see cref="Calculate"/> null döner — uydurma tahmin üretilmez.
/// </summary>
public static class PaceCalculator
{
    private static readonly TimeSpan SoonThreshold = TimeSpan.FromMinutes(5);

    public static PaceResult? Calculate(UsageWindow window, DateTimeOffset now)
    {
        // Tükenmiş pencerede tempo değil rozet gösterilir; boş pencerede hiç satır yok.
        if (window.Percent >= 100) return null;
        if (window.Percent <= 0) return null;
        if (window.ResetsAt is null) return null;
        if (window.WindowLength is not { } length || length <= TimeSpan.Zero) return null;

        var remaining = window.ResetsAt.Value - now;
        var elapsedRatio = Math.Clamp((length - remaining) / length, 0, 1);
        if (elapsedRatio <= 0) return null; // pencere yeni sıfırlandı, anlamlı oran yok

        var used = window.Percent / 100.0;
        var tempo = used - elapsedRatio;

        var depletesIn = length * elapsedRatio * (1 / used - 1);
        if (depletesIn < TimeSpan.Zero) depletesIn = TimeSpan.Zero;

        return new PaceResult(tempo, depletesIn);
    }

    public static string Format(UsageWindow window, PaceResult pace, DateTimeOffset now)
    {
        var remaining = window.ResetsAt!.Value - now;

        if (pace.Tempo > 0.05)
        {
            // Önden gitme matematiksel olarak her zaman pencere bitmeden tükenir;
            // bu dal çelişkiye karşı güvenlik ağıdır (kayan nokta tozu).
            if (pace.DepletesIn >= remaining) return "Rahat · pencere sonuna yeter";
            if (pace.DepletesIn < SoonThreshold) return "Hızlı gidiyorsun · bu tempoda birazdan biter";
            return $"Hızlı gidiyorsun · bu tempoda {FormatDuration(pace.DepletesIn)} sonra biter";
        }

        if (pace.Tempo < -0.05) return "Rahat · pencere sonuna yeter";

        return "Dengeli tempo";
    }

    /// <summary>%100 pencerede tempo yerine gösterilen rozet yazısı.</summary>
    public static string FormatConsumedBadge(UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } reset) return "Tükendi";
        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero) return "Tükendi · sıfırlandı";
        return $"Tükendi · {FormatDuration(remaining)} sonra sıfırlanır";
    }

    /// <summary>"53 dk", "2 sa 10 dk", "2 gün 1 sa" — süreleri kısa yazar.</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk";

        if (span.TotalDays < 1)
        {
            var hours = (int)span.TotalHours;
            return span.Minutes > 0 ? $"{hours} sa {span.Minutes} dk" : $"{hours} sa";
        }

        var days = (int)span.TotalDays;
        return span.Hours > 0 ? $"{days} gün {span.Hours} sa" : $"{days} gün";
    }
}
