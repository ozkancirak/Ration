using Kalan.Core.Model;
using Kalan.Core.Providers.Claude;
using Kalan.Core.Providers.Codex;
using Kalan.Core.Usage;

namespace Kalan.Tests;

/// <summary>Tempo hesabı testleri. Tüm veriler sentetiktir; kimlik dosyası okunmaz.</summary>
public class PaceCalculatorTests
{
    private static UsageWindow Window(double percent, TimeSpan length, TimeSpan remainingFromNow)
    {
        var now = DateTimeOffset.UtcNow;
        return new UsageWindow(
            WindowKind.Session, percent, 100, percent,
            now.Add(remainingFromNow), "5 saatlik", length);
    }

    private static string Line(UsageWindow window)
    {
        var now = DateTimeOffset.UtcNow;
        var pace = PaceCalculator.Calculate(window, now);
        Assert.NotNull(pace);
        return PaceCalculator.Format(window, pace!, now);
    }

    [Fact]
    public void Gerideyse_RahatYazar()
    {
        // 5 saatlik pencerenin 1 saati geçti (%20), tüketim %2 → tempo −0.18.
        Assert.Equal(
            "Rahat · pencere sonuna yeter",
            Line(Window(2, TimeSpan.FromHours(5), TimeSpan.FromHours(4))));
    }

    [Fact]
    public void Hizliysa_BitisSuresiyleYazar()
    {
        // 10 saatlik pencerenin 8 saati geçti (%80), tüketim %90 → tempo +0.10,
        // tükenme ≈ 53 dk < kalan 2 sa.
        Assert.Equal(
            "Hızlı gidiyorsun · bu tempoda 53 dk sonra biter",
            Line(Window(90, TimeSpan.FromHours(10), TimeSpan.FromHours(2))));
    }

    [Fact]
    public void Bitis_Yakinsa_BirazdanYazar_AslaSifirDakikaYok()
    {
        // 5 saatlik pencerenin 4 saati geçti (%80), tüketim %98 → tükenme ≈ 5 dk altı.
        var text = Line(Window(98, TimeSpan.FromHours(5), TimeSpan.FromHours(1)));

        Assert.Equal("Hızlı gidiyorsun · bu tempoda birazdan biter", text);
        Assert.DoesNotContain("0 dk", text);
    }

    [Fact]
    public void Bitis_KalandanUzunsa_CeliskiOlmaz_RahatYazar()
    {
        // Sentetik sınır durumu: tempo önden ama tahmin kalandan uzun → rahat.
        var now = DateTimeOffset.UtcNow;
        var window = Window(60, TimeSpan.FromHours(10), TimeSpan.FromHours(1));
        var pace = new PaceResult(0.10, TimeSpan.FromHours(2));

        Assert.Equal("Rahat · pencere sonuna yeter", PaceCalculator.Format(window, pace, now));
    }

    [Fact]
    public void DengeYakinsa_DengeliYazar()
    {
        // Tüketim tam zamanında: %50 / %50 → tempo 0.
        Assert.Equal(
            "Dengeli tempo",
            Line(Window(50, TimeSpan.FromHours(10), TimeSpan.FromHours(5))));
    }

    [Theory]
    [InlineData(100)]   // tükenmiş → rozet, tempo satırı yok
    [InlineData(140)]   // kırpılmamış ham değer de gizler
    [InlineData(0)]     // hiç tüketim yok
    public void Ucta_SatirGosterilmez(double percent)
    {
        Assert.Null(PaceCalculator.Calculate(
            Window(percent, TimeSpan.FromHours(5), TimeSpan.FromHours(1)), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TukendiRozeti_KalanSureyleYazar()
    {
        var now = DateTimeOffset.UtcNow;
        var window = Window(100, TimeSpan.FromHours(5), TimeSpan.FromHours(4) + TimeSpan.FromMinutes(59));

        Assert.Equal("Tükendi · 4 sa 59 dk sonra sıfırlanır", PaceCalculator.FormatConsumedBadge(window, now));
    }

    [Fact]
    public void TukendiRozeti_Sifirlamasizsa_SadeYazar()
    {
        var window = new UsageWindow(WindowKind.Session, 100, 100, 100, null, "5 saatlik", TimeSpan.FromHours(5));

        Assert.Equal("Tükendi", PaceCalculator.FormatConsumedBadge(window, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EksikVeride_SatirGosterilmez(bool withReset, bool withLength)
    {
        var now = DateTimeOffset.UtcNow;
        var window = new UsageWindow(
            WindowKind.Session, 90, 100, 90,
            withReset ? now.AddHours(1) : null,
            "5 saatlik",
            withLength ? TimeSpan.FromHours(5) : null);

        Assert.Null(PaceCalculator.Calculate(window, now));
    }

    [Fact]
    public void Pencere_YeniSifirlandiysa_SatirGosterilmez()
    {
        // Kalan süre pencere uzunluğuna eşit ya da fazla → geçen oran <= 0.
        Assert.Null(PaceCalculator.Calculate(
            Window(90, TimeSpan.FromHours(5), TimeSpan.FromHours(6)), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CokYeniPencerede_GecmisOraniYetersizse_SatirGosterilmez()
    {
        Assert.Null(PaceCalculator.Calculate(
            Window(90, TimeSpan.FromHours(5), TimeSpan.FromHours(4) + TimeSpan.FromMinutes(52)),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Sure_Bicimlendirme_KisaYazar()
    {
        Assert.Equal("5 dk", PaceCalculator.FormatDuration(TimeSpan.FromMinutes(5)));
        Assert.Equal("53 dk", PaceCalculator.FormatDuration(TimeSpan.FromMinutes(53)));
        Assert.Equal("2 sa 10 dk", PaceCalculator.FormatDuration(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(10)));
        Assert.Equal("3 sa", PaceCalculator.FormatDuration(TimeSpan.FromHours(3)));
        Assert.Equal("2 gün 1 sa", PaceCalculator.FormatDuration(TimeSpan.FromDays(2) + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ClaudeParser_PencereUzunlugunuDoldurur()
    {
        const string json = """{ "five_hour": { "utilization": 10 }, "seven_day": 20 }""";

        var windows = ClaudeUsageParser.ParseWindows(json);

        Assert.Equal(TimeSpan.FromHours(5), Assert.Single(windows, w => w.Kind == WindowKind.Session).WindowLength);
        Assert.All(windows.Where(w => w.Kind == WindowKind.Weekly), w => Assert.Equal(TimeSpan.FromDays(7), w.WindowLength));
    }

    [Fact]
    public void CodexParser_PencereUzunlugunuYanttanOkur()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window":   { "used_percent": 10, "limit_window_seconds": 18000 },
            "secondary_window": { "used_percent": 20, "limit_window_seconds": 604800 }
          }
        }
        """;

        var windows = CodexUsageParser.ParseWindows(json);

        Assert.Equal(TimeSpan.FromHours(5), Assert.Single(windows, w => w.Kind == WindowKind.Session).WindowLength);
        Assert.Equal(TimeSpan.FromDays(7), Assert.Single(windows, w => w.Kind == WindowKind.Weekly).WindowLength);
    }
}
