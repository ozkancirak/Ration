using Ration.Core.Model;
using Ration.Core.Providers;
using Ration.Core.Providers.Codex;

namespace Ration.Tests;

/// <summary>
/// Uses the SHAPE of the wham/usage response verified on 2026-09-19.
/// All values in it are made up — identity, email or quota data copied from a real account
/// cannot enter the repo.
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
    public void MainWindows_DeriveTheNameFromTheWindowLength()
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
    public void ResetAt_IsReadAsEpochSeconds()
    {
        var usage = CodexUsageParser.Parse(GercekSekilliYanit);

        var session = Assert.Single(usage.Windows, w => w.Label == "5 saatlik");

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1789851653),
            session.ResetsAt);
    }

    [Fact]
    public void PerModelExtraQuotas_AreAddedAsWindows()
    {
        var usage = CodexUsageParser.Parse(GercekSekilliYanit);

        var reserve = Assert.Single(usage.Windows, w => w.Label == "gpt-reserve · Haftalık");

        Assert.Equal(100, reserve.Percent);
        Assert.Equal(WindowKind.Weekly, reserve.Kind);
    }

    [Fact]
    public void PlanNameAndCredits_AreRead()
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
    public void WindowName_IsDerivedFromTheDuration(double saniye, string beklenen)
    {
        Assert.Equal(beklenen, CodexUsageParser.DescribeWindow(saniye, WindowKind.Session));
    }

    [Fact]
    public void UnknownDuration_FallsBackToTheKind()
    {
        Assert.Equal("Haftalık", CodexUsageParser.DescribeWindow(null, WindowKind.Weekly));
        Assert.Equal("Oturum", CodexUsageParser.DescribeWindow(0, WindowKind.Session));
    }
}

public class RawResponseRedactorTests
{
    [Fact]
    public void IdentityFields_AreMasked()
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

        // Quota data must stay as it is, otherwise the diagnostics are useless.
        Assert.Contains("plus", redacted);
        Assert.Contains(RawResponseRedactor.Placeholder, redacted);
    }

    [Fact]
    public void NestedObjectsAndArrays_AreMaskedToo()
    {
        const string json = """
        { "data": [ { "email": "ornek@example.com", "used_percent": 42 } ] }
        """;

        var redacted = RawResponseRedactor.Redact(json);

        Assert.DoesNotContain("ornek@example.com", redacted);
        Assert.Contains("42", redacted);
    }

    [Fact]
    public void UnparsableResponse_IsNeverShown()
    {
        var redacted = RawResponseRedactor.Redact("bu json degil <html>ornek@example.com</html>");

        Assert.DoesNotContain("ornek@example.com", redacted);
    }

    [Fact]
    public void TokenFields_AreMasked()
    {
        const string json = """
        {
          "csrf_token": "sentetik-csrf",
          "host_bridge_token": "sentetik-bridge",
          "bucketId": "sentetik-bucket",
          "plan_type": "plus"
        }
        """;

        var redacted = RawResponseRedactor.Redact(json);

        Assert.DoesNotContain("sentetik-csrf", redacted);
        Assert.DoesNotContain("sentetik-bridge", redacted);
        Assert.DoesNotContain("sentetik-bucket", redacted);
        Assert.Contains("plus", redacted);
    }
}
