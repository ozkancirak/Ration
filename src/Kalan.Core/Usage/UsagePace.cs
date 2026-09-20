using Kalan.Core.Model;

namespace Kalan.Core.Usage;

/// <summary>Tempo hesabı sonucu: kota pencere bitmeden tükenir mi?</summary>
/// <param name="TempoPoints">tempo * 100, yüzde puanı. Negatif = geride (iyi), pozitif = önde (risk).</param>
/// <param name="WillLast">true ise tüketim pencerenin gerisinde → "yetecek".</param>
/// <param name="DepletesIn">WillLast false iken mevcut tempoyla tükenmeye kalan süre tahmini.</param>
public sealed record PaceResult(double TempoPoints, bool WillLast, TimeSpan? DepletesIn);

/// <summary>
/// "Kotam pencere bitmeden tükenir mi?" sorusunun saf hesabı.
/// UI'da hesaplama yapılmaz; burası çağrılır, sonucu yazı olarak <see cref="Format"/> verir.
/// Gösterilmeyecek durumlarda null döner — uydurma tahmin üretilmez.
/// </summary>
public static class UsagePace
{
    public static PaceResult? Calculate(UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is null) return null;
        if (window.WindowLength is not { } length || length <= TimeSpan.Zero) return null;

        var remaining = window.ResetsAt.Value - now;
        var elapsedRatio = (length - remaining) / length;
        if (elapsedRatio <= 0) return null; // pencere yeni sıfırlandı, anlamlı oran yok

        var used = window.Percent / 100.0;
        var tempo = used - elapsedRatio;

        // Hiç tüketim yoksa tükenme tahmini tanımsızdır; satır "yetecek" yazar.
        if (tempo <= 0 || used <= 0) return new PaceResult(tempo * 100, true, null);

        var depletesIn = length * elapsedRatio * (1 / used - 1);
        if (depletesIn < TimeSpan.Zero) depletesIn = TimeSpan.Zero;

        return new PaceResult(tempo * 100, false, depletesIn);
    }

    public static string Format(PaceResult pace)
    {
        var points = (int)Math.Round(pace.TempoPoints);

        if (pace.WillLast) return $"Tempo: geride (%{points}) · yetecek";

        return $"Tempo: önde (%+{points}) · ~{FormatDuration(pace.DepletesIn ?? TimeSpan.Zero)} sonra tükenir";
    }

    /// <summary>"3 sa", "4 sa 12 dk", "2 gün 1 sa" — süreleri kısa yazar.</summary>
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
