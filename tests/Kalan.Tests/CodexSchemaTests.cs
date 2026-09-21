using Kalan.Core.Model;
using Kalan.Core.Providers;
using Kalan.Core.Providers.Codex;

namespace Kalan.Tests;

/// <summary>
/// wham/usage yanıtının 19.09.2026'da doğrulanmış ŞEKLİNİ kullanır.
/// İçindeki bütün değerler uydurmadır — gerçek hesaptan kopyalanmış kimlik,
/// e-posta veya kota verisi repoya giremez (AGENTS.md §2.3).
/// </summary>
public class CodexSchemaTests
{
    private const string GercekSekilliYanit = """
    {
      "user_id": "user-SENTETIK",
      "account_id": "00000000-0000-0000-0000-000000000000",
      "email": "ornek@example.com",
      "plan_type": "plus",
      "rate_limit": {
        "allowed": true,
        "limit_reached": false,
        "primary_window": {
          "used_percent": 1,
          "limit_window_seconds": 18000,
          "reset_after_seconds": 8272,
          "reset_at": 1789851653
        },
        "secondary_window": {
          "used_percent": 69,
          "limit_window_seconds": 604800,
          "reset_after_seconds": 136626,
          "reset_at": 1789980007
        }
      },
      "additional_rate_limits": [
        {
          "limit_name": "gpt-reserve",
          "metered_feature": "base_model_inference",
          "rate_limit": {
            "allowed": false,
            "limit_reached": true,
            "primary_window": {
              "used_percent": 100,
              "limit_window_seconds": 604800,
              "reset_after_seconds": 143366,
              "reset_at": 1789986747
            },
            "secondary_window": null
          },
          "normal_model_slug": "ornek-model"
        }
      ],
      "credits": { "has_credits": false, "unlimited": false, "balance": "0" }
    }
    """;

    [Fact]
    public void AnaPencerelerAdiniPencereUzunlugundanTuretir()
    {
        var usage = CodexUsageParser.Parse(GercekSekilliYanit);

        var session = Assert.Single(usage.Windows, w => w.Label == "5 saatlik");
        Assert.Equal(WindowKind.Session, session.Kind);
        Assert.Equal(1, session.Percent);

        var weekly = Assert.Single(usage.Windows, w => w.Label == "Haftalık");
        Assert.Equal(WindowKind.Weekly, weekly.Kind);
        Assert.Equal(69, weekly.Percent);
    }

    [Fact]
    public void ResetAtEpochSaniyeOlarakOkunur()
    {
        var usage = CodexUsageParser.Parse(GercekSekilliYanit);

        var session = Assert.Single(usage.Windows, w => w.Label == "5 saatlik");

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1789851653),
            session.ResetsAt);
    }

    [Fact]
    public void ModelBazliEkKotalarPencereOlarakEklenir()
    {
        var usage = CodexUsageParser.Parse(GercekSekilliYanit);

        var reserve = Assert.Single(usage.Windows, w => w.Label == "gpt-reserve · Haftalık");

        Assert.Equal(100, reserve.Percent);
        Assert.Equal(WindowKind.Weekly, reserve.Kind);
    }

    [Fact]
    public void PlanAdiVeKrediOkunur()
    {
        var usage = CodexUsageParser.Parse(GercekSekilliYanit);

        Assert.Equal("plus", usage.PlanName);
        Assert.NotNull(usage.Credits);
        Assert.Equal(0m, usage.Credits!.RemainingCredits);
    }

    [Theory]
    [InlineData(18000, "5 saatlik")]
    [InlineData(604800, "Haftalık")]
    [InlineData(86400, "Günlük")]
    [InlineData(2592000, "Aylık")]
    [InlineData(259200, "3 günlük")]
    public void PencereAdiSureyeGoreTuretilir(double saniye, string beklenen)
    {
        Assert.Equal(beklenen, CodexUsageParser.DescribeWindow(saniye, WindowKind.Session));
    }

    [Fact]
    public void SureBilinmiyorsaTureDuser()
    {
        Assert.Equal("Haftalık", CodexUsageParser.DescribeWindow(null, WindowKind.Weekly));
        Assert.Equal("Oturum", CodexUsageParser.DescribeWindow(0, WindowKind.Session));
    }
}

public class RawResponseRedactorTests
{
    [Fact]
    public void KimlikAlanlariniMaskeler()
    {
        const string json = """
        {
          "email": "ornek@example.com",
          "user_id": "user-SENTETIK",
          "account_id": "00000000-0000-0000-0000-000000000000",
          "plan_type": "plus"
        }
        """;

        var redacted = RawResponseRedactor.Redact(json);

        Assert.DoesNotContain("ornek@example.com", redacted);
        Assert.DoesNotContain("user-SENTETIK", redacted);
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", redacted);

        // Kota verisi olduğu gibi kalmalı, yoksa tanı işe yaramaz.
        Assert.Contains("plus", redacted);
        Assert.Contains(RawResponseRedactor.Placeholder, redacted);
    }

    [Fact]
    public void IcIceGecmisNesneVeDizilerdeDeMaskeler()
    {
        const string json = """
        { "data": [ { "email": "ornek@example.com", "used_percent": 42 } ] }
        """;

        var redacted = RawResponseRedactor.Redact(json);

        Assert.DoesNotContain("ornek@example.com", redacted);
        Assert.Contains("42", redacted);
    }

    [Fact]
    public void AyristirilamayanYanitiHicGostermez()
    {
        var redacted = RawResponseRedactor.Redact("bu json degil <html>ornek@example.com</html>");

        Assert.DoesNotContain("ornek@example.com", redacted);
    }

    [Fact]
    public void TokenAlanlariniMaskeler()
    {
        const string json = """
        {
          "csrf_token": "sentetik-csrf",
          "host_bridge_token": "sentetik-bridge",
          "plan_type": "plus"
        }
        """;

        var redacted = RawResponseRedactor.Redact(json);

        Assert.DoesNotContain("sentetik-csrf", redacted);
        Assert.DoesNotContain("sentetik-bridge", redacted);
        Assert.Contains("plus", redacted);
    }
}
