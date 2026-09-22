using Ration.Core.Discovery;

namespace Ration.Tests;

/// <summary>
/// Keşif testleri. Tüm dizinler SENTETİKTİR, temp altında üretilir.
/// En önemli iddia: hiçbir DEĞER (token/e-posta/id) çıktığa sızmaz.
/// </summary>
public class ProviderDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ration-kesif-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void JsonSemasi_TurleriYazar_DegerleriYazmaz()
    {
        Directory.CreateDirectory(_dir);
        const string secretMail = "gizli-kullanici-orn-ek@example.com";
        const string secretToken = "gizli-token-abc123XYZ";
        File.WriteAllText(Path.Combine(_dir, "ayarlar.json"), $$"""
            {
              "kullanici": { "eposta": "{{secretMail}}", "yas": 41, "aktif": true },
              "oturum": { "token": "{{secretToken}}", "etiketler": ["a", "b"] }
            }
            """);

        var report = ProviderDiscovery.Discover("gemini", _dir);
        var dump = string.Join("\n", report.Files.SelectMany(f => f.Schema ?? Array.Empty<string>()));

        Assert.Contains("kullanici.eposta : String(len=", dump);
        Assert.Contains("kullanici.yas : Number", dump);
        Assert.Contains("kullanici.aktif : True", dump);
        Assert.Contains("oturum.etiketler[] : String(len=1)", dump);

        // Değer sızıntısı yok: ne e-posta, ne token, ne de sayı değeri.
        Assert.DoesNotContain(secretMail, dump);
        Assert.DoesNotContain(secretToken, dump);
        Assert.DoesNotContain("41", dump);
    }

    [Fact]
    public void JsonlDosyalari_Orneklenir_DegerYazilmaz()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Path.Combine(_dir, "oturum.jsonl"), new[]
        {
            """{"type":"prompt","metin":"gizli-prompt-icerigi-zzz"}""",
            """{"type":"kullanim","girdi":123,"cikti":456}""",
        });

        var report = ProviderDiscovery.Discover("copilot", _dir);
        var file = Assert.Single(report.Files);
        var dump = string.Join("\n", file.Schema ?? Array.Empty<string>());

        Assert.Contains("metin : String(len=", dump);
        Assert.Contains("girdi : Number", dump);
        Assert.DoesNotContain("gizli-prompt-icerigi-zzz", dump);
    }

    [Fact]
    public void JsonOlmayanDosyalar_YalnizcaYolVeBoyutlaListelenir()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "notlar.txt"), "gizli-not-icerigi-qqq");

        var report = ProviderDiscovery.Discover("gemini", _dir);
        var file = Assert.Single(report.Files);

        Assert.Null(file.Schema);
        Assert.True(file.Size > 0);
    }

    [Fact]
    public void OlmayanDizin_ZarifceBildirilir()
    {
        var report = ProviderDiscovery.Discover(
            "gemini", Path.Combine(Path.GetTempPath(), $"ration-yok-{Guid.NewGuid():N}"));

        Assert.Empty(report.Files);
        Assert.NotEmpty(report.Notes);
    }

    [Fact]
    public void BilinmeyenSaglayicininKokuYoktur()
    {
        Assert.Null(ProviderDiscovery.RootsFor("bilinmeyen-saglayici"));
        Assert.NotNull(ProviderDiscovery.RootsFor("gemini"));
        Assert.NotNull(ProviderDiscovery.RootsFor("copilot"));
    }
}
