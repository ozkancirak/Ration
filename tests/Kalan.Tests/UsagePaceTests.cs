using Kalan.Core.Model;
using Kalan.Core.Providers.Claude;
using Kalan.Core.Providers.Codex;
using Kalan.Core.Usage;

namespace Kalan.Tests;

/// <summary>Tempo hesabı testleri. Tüm veriler sentetiktir; kimlik dosyası okunmaz.</summary>
public class UsagePaceTests
{
    private static UsageWindow Window(double percent, TimeSpan length, TimeSpan remainingFromNow)
    {
        var now = DateTimeOffset.UtcNow;
        return new UsageWindow(
            WindowKind.Session, percent, 100, percent,
            now.Add(remainingFromNow), "5 saatlik", length);
    }

    [Fact]
    public void Gerideyse_YetecekYazar()
    {
        // 5 saatlik pencerenin 1 saati geçti (%20), tüketim %2 → tempo -18 puan.
        var pace = UsagePace.Calculate(Window(2, TimeSpan.FromHours(5), TimeSpan.FromHours(4)), DateTimeOffset.UtcNow);

        Assert.NotNull(pace);
        Assert.True(pace!.WillLast);
        Assert.Equal(-18, Math.Round(pace.TempoPoints));
        Assert.Null(pace.DepletesIn);
        Assert.Equal("Tempo: geride (%-18) · yetecek", UsagePace.Format(pace));
    }

    [Fact]
    public void Ondyse_TukenmeTahminiUretir()
    {
        // 5 saatlik pencerenin 4 saati geçti (%80), tüketim %98 → tempo +18 puan.
        var pace = UsagePace.Calculate(Window(98, TimeSpan.FromHours(5), TimeSpan.FromHours(1)), DateTimeOffset.UtcNow);

        Assert.NotNull(pace);
        Assert.False(pace!.WillLast);
        Assert.Equal(18, Math.Round(pace.TempoPoints));
        Assert.NotNull(pace.DepletesIn);
        Assert.StartsWith("Tempo: önde (%+18) · ~", UsagePace.Format(pace));
        Assert.EndsWith("sonra tükenir", UsagePace.Format(pace));
    }

    [Fact]
    public void Tuketim_Sifirsa_YetecekYazar()
    {
        var pace = UsagePace.Calculate(Window(0, TimeSpan.FromHours(5), TimeSpan.FromHours(1)), DateTimeOffset.UtcNow);

        Assert.NotNull(pace);
        Assert.True(pace!.WillLast);
        Assert.Equal("Tempo: geride (%-80) · yetecek", UsagePace.Format(pace));
    }

    [Fact]
    public void Tempo_Sifirsa_GerideSayilir()
    {
        // Tüketim tam zamanında: %50 / %50 → tempo 0 → "geride".
        var pace = UsagePace.Calculate(Window(50, TimeSpan.FromHours(10), TimeSpan.FromHours(5)), DateTimeOffset.UtcNow);

        Assert.NotNull(pace);
        Assert.True(pace!.WillLast);
        Assert.Equal("Tempo: geride (%0) · yetecek", UsagePace.Format(pace));
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

        Assert.Null(UsagePace.Calculate(window, now));
    }

    [Fact]
    public void Pencere_YeniSifirlandiysa_SatirGosterilmez()
    {
        // Kalan süre pencere uzunluğuna eşit ya da fazla → geçen oran <= 0.
        var pace = UsagePace.Calculate(Window(90, TimeSpan.FromHours(5), TimeSpan.FromHours(6)), DateTimeOffset.UtcNow);

        Assert.Null(pace);
    }

    [Fact]
    public void Sure_Bicimlendirme_KisaYazar()
    {
        Assert.Equal("5 dk", UsagePace.FormatDuration(TimeSpan.FromMinutes(5)));
        Assert.Equal("3 sa", UsagePace.FormatDuration(TimeSpan.FromHours(3)));
        Assert.Equal("4 sa 12 dk", UsagePace.FormatDuration(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(12)));
        Assert.Equal("2 gün 1 sa", UsagePace.FormatDuration(TimeSpan.FromDays(2) + TimeSpan.FromHours(1)));
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
