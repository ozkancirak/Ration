using Ration.Core.Cost;
using Ration.Core.Model;
using System.Text.Json;

namespace Ration.Tests;

/// <summary>
/// Tüm JSONL içerikleri SENTETİKTİR. Gerçek oturum loglarından kopyalanmış
/// hiçbir şey repoya giremez (AGENTS.md §2.3).
///
/// NOT: JSON şablonlarında interpolasyon KULLANILMIYOR. Raw string interpolasyonunda
/// ($$"""...""") JSON'un kapanış süslü parantezleri ("}}}") hole kapatma dizisiyle
/// çakışıyor; bunun yerine düz şablon + Replace kullanıyoruz.
/// </summary>
public sealed class ClaudeCostScannerTests : IDisposable
{
    private const string AssistantTemplate = """
    {"type":"assistant","requestId":"__RID__","message":{"id":"__MID__","model":"__MODEL__","usage":{"input_tokens":__IN__,"output_tokens":__OUT__,"cache_read_input_tokens":__CR__,"cache_creation_input_tokens":__CC__}}}
    """;

    private const string TimestampedTemplate = """
    {"type":"assistant","timestamp":"__TS__","requestId":"__RID__","message":{"id":"__MID__","model":"__MODEL__","usage":{"input_tokens":__IN__,"output_tokens":0}}}
    """;

    private readonly string _dir;

    public ClaudeCostScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ration-cost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "proje-a"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteJsonl(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_dir, "proje-a", name), lines);

    private static string AssistantLine(
        string messageId,
        string requestId,
        string model,
        int input,
        int output,
        int cacheRead = 0,
        int cacheCreate = 0) =>
        AssistantTemplate
            .Replace("__RID__", requestId)
            .Replace("__MID__", messageId)
            .Replace("__MODEL__", model)
            .Replace("__IN__", input.ToString())
            .Replace("__OUT__", output.ToString())
            .Replace("__CR__", cacheRead.ToString())
            .Replace("__CC__", cacheCreate.ToString());

    private static string TimestampedLine(
        DateTimeOffset timestamp,
        string messageId,
        string requestId,
        int input) =>
        TimestampedTemplate
            .Replace("__TS__", timestamp.ToString("o"))
            .Replace("__RID__", requestId)
            .Replace("__MID__", messageId)
            .Replace("__MODEL__", "ornek-model")
            .Replace("__IN__", input.ToString());

    [Fact]
    public void AsistanSatirlarininTokenlariniToplar()
    {
        WriteJsonl("a.jsonl",
            AssistantLine("msg_1", "req_1", "ornek-model", 100, 50, cacheRead: 10, cacheCreate: 5),
            AssistantLine("msg_2", "req_2", "ornek-model", 200, 60));

        var result = ClaudeCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);

        var model = Assert.Single(result.Tally.Models);

        Assert.Equal("ornek-model", model.Model);
        Assert.Equal(300, model.InputTokens);
        Assert.Equal(110, model.OutputTokens);
        Assert.Equal(10, model.CacheReadTokens);
        Assert.Equal(5, model.CacheCreationTokens);
        Assert.Equal(425, model.TotalTokens);
    }

    [Fact]
    public void ClaudeCacheOkumaNormalGirdidenAyridir()
    {
        WriteJsonl("a.jsonl",
            AssistantLine("msg_1", "req_1", "ornek-model", 100, 10, cacheRead: 80));

        var result = ClaudeCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);
        var model = Assert.Single(result.Tally.Models);

        Assert.Equal(100, model.InputTokens);
        Assert.Equal(80, model.CacheReadTokens);
        Assert.Equal(190, model.TotalTokens);
    }

    [Fact]
    public void ClaudeOlayZamaniDosyaMtimeindanOnceliklidir()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        WriteJsonl("a.jsonl", TimestampedLine(now, "msg_1", "req_1", 7));
        File.SetLastWriteTimeUtc(
            Path.Combine(_dir, "proje-a", "a.jsonl"),
            DateTime.UtcNow.AddDays(-40));

        var result = ClaudeCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);

        Assert.Equal(7, Assert.Single(result.Tally.Models).InputTokens);
        Assert.True(result.PeriodKnown);
    }

    [Fact]
    public void AyniYanitiIkiKezSaymaz()
    {
        // Ayni message.id + requestId iki satirda gorunebilir (devam kaydi, yeniden yazim).
        WriteJsonl("a.jsonl",
            AssistantLine("msg_1", "req_1", "ornek-model", 100, 50),
            AssistantLine("msg_1", "req_1", "ornek-model", 100, 50));

        var result = ClaudeCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);

        var model = Assert.Single(result.Tally.Models);

        Assert.Equal(100, model.InputTokens);
        Assert.Equal(1, result.Tally.EntryCount);
    }

    [Fact]
    public void FarkliRequestIdAyriSayilir()
    {
        WriteJsonl("a.jsonl",
            AssistantLine("msg_1", "req_1", "ornek-model", 100, 0),
            AssistantLine("msg_1", "req_2", "ornek-model", 100, 0));

        var result = ClaudeCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);

        Assert.Equal(200, Assert.Single(result.Tally.Models).InputTokens);
    }

    [Fact]
    public void AsistanOlmayanVeBozukSatirlariAtlar()
    {
        WriteJsonl("a.jsonl",
            """{"type":"user","message":{"content":"merhaba"}}""",
            "bu json degil",
            """{"type":"assistant","message":{"id":"msg_x"}}""",
            AssistantLine("msg_1", "req_1", "ornek-model", 10, 5));

        var result = ClaudeCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);

        Assert.Equal(10, Assert.Single(result.Tally.Models).InputTokens);
    }

    [Fact]
    public void DonemDisiSatirlariAtlar()
    {
        WriteJsonl("a.jsonl",
            TimestampedLine(DateTimeOffset.UtcNow.AddDays(-40), "msg_1", "req_1", 999),
            TimestampedLine(DateTimeOffset.UtcNow.AddHours(-1), "msg_2", "req_2", 7));

        var result = ClaudeCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-7), _dir);

        Assert.Equal(7, Assert.Single(result.Tally.Models).InputTokens);
    }

    [Fact]
    public void OlmayanDizinIcinBosSonucDoner()
    {
        var result = ClaudeCostScanner.Scan(
            DateTimeOffset.UtcNow.AddDays(-1),
            Path.Combine(Path.GetTempPath(), $"ration-yok-{Guid.NewGuid():N}"));

        Assert.True(result.Tally.IsEmpty);
        Assert.NotNull(result.Note);
    }
}

public sealed class CodexCostScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ration-codex-cost-{Guid.NewGuid():N}");

    public CodexCostScannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteJsonl(params string[] lines) =>
        File.WriteAllLines(Path.Combine(_dir, "session.jsonl"), lines);

    private static string TokenLine(
        DateTimeOffset? at,
        int input,
        int cacheRead,
        int output,
        bool cumulative = false,
        string model = "codex-model")
    {
        var usage = new
        {
            input_tokens = input,
            cached_input_tokens = cacheRead,
            output_tokens = output,
            reasoning_output_tokens = 0,
        };
        var info = new Dictionary<string, object?>
        {
            [cumulative ? "total_token_usage" : "last_token_usage"] = usage,
        };
        var payload = new
        {
            type = "token_count",
            model,
            info,
        };
        var line = new Dictionary<string, object?>
        {
            ["type"] = "event_msg",
            ["payload"] = payload,
        };
        if (at is { } timestamp) line["timestamp"] = timestamp;
        return JsonSerializer.Serialize(line);
    }

    [Fact]
    public void CodexCacheGirdisiniNormalGirdidenAyrir()
    {
        WriteJsonl(TokenLine(DateTimeOffset.UtcNow.AddMinutes(-1), 100, 80, 10));

        var result = CodexCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);
        var model = Assert.Single(result.Tally.Models);

        Assert.Equal(20, model.InputTokens);
        Assert.Equal(80, model.CacheReadTokens);
        Assert.Equal(10, model.OutputTokens);
        Assert.Equal(110, model.TotalTokens);
    }

    [Fact]
    public void TekrarlananLastUsageIkiKezSayilmaz()
    {
        var line = TokenLine(DateTimeOffset.UtcNow.AddMinutes(-1), 100, 80, 10);
        WriteJsonl(line, line);

        var result = CodexCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);

        Assert.Equal(110, Assert.Single(result.Tally.Models).TotalTokens);
    }

    [Fact]
    public void KumulatifToplamYalnizcaDonemIlerlemesiniSayar()
    {
        var since = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        WriteJsonl(
            TokenLine(since.AddHours(-1), 100, 80, 10, cumulative: true),
            TokenLine(DateTimeOffset.UtcNow.AddMinutes(-1), 120, 90, 15, cumulative: true),
            TokenLine(DateTimeOffset.UtcNow.AddSeconds(-30), 120, 90, 15, cumulative: true));

        var result = CodexCostScanner.Scan(since, _dir);

        // (120-100) - (90-80) normal girdidir: 10 + 10 cache + 5 çıktı.
        Assert.Equal(25, Assert.Single(result.Tally.Models).TotalTokens);
        Assert.True(result.PeriodKnown);
    }

    [Fact]
    public void OlayZamaniDosyaMtimeindanOnceliklidir()
    {
        var current = DateTimeOffset.UtcNow.AddMinutes(-1);
        WriteJsonl(
            TokenLine(current.AddDays(-40), 500, 0, 0),
            TokenLine(current, 7, 0, 0));
        File.SetLastWriteTimeUtc(
            Path.Combine(_dir, "session.jsonl"),
            DateTime.UtcNow.AddDays(-40));

        var result = CodexCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);

        Assert.Equal(7, Assert.Single(result.Tally.Models).InputTokens);
        Assert.True(result.PeriodKnown);
    }

    [Fact]
    public void ZamanDamgasiYoksaDonemBelirsizIsaretlenir()
    {
        WriteJsonl(TokenLine(null, 7, 0, 0));

        var result = CodexCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-1), _dir);

        Assert.False(result.PeriodKnown);
    }
}

public class PricingTests
{
    [Fact]
    public void EnUzunOnekEslesmesiKazanir()
    {
        var table = new PricingTable(new Dictionary<string, ModelRate>
        {
            ["ornek"] = new(1m, 1m, 0m, 0m),
            ["ornek-buyuk"] = new(10m, 20m, 0m, 0m),
        });

        Assert.Equal(10m, table.Find("ornek-buyuk-2026")!.InputPerMillion);
        Assert.Equal(1m, table.Find("ornek-kucuk")!.InputPerMillion);
        Assert.Null(table.Find("baska-marka"));
    }

    [Fact]
    public void FiyatiBilinmeyenModelMaliyeteKatilmazAmaRaporlanir()
    {
        var tally = new TokenTally();
        tally.Add("fiyatli-model", 1_000_000, 1_000_000, 0, 0);
        tally.Add("fiyatsiz-model", 5_000_000, 0, 0, 0);

        var scan = new CostScanResult(tally, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, 1);

        var pricing = new PricingTable(new Dictionary<string, ModelRate>
        {
            ["fiyatli-model"] = new(3m, 15m, 0m, 0m),
        });

        var report = CostEstimator.Estimate(scan, pricing);

        Assert.Equal(18m, report.TotalCost);
        Assert.Equal("fiyatsiz-model", Assert.Single(report.ModelsWithoutPricing!));

        // Token'lar fiyat bilinmese de eksiksiz raporlanir.
        Assert.Equal(6_000_000, report.InputTokens);
    }

    [Fact]
    public void BosTabloylaMaliyetSifirdirAmaTokenlarDurur()
    {
        var tally = new TokenTally();
        tally.Add("herhangi-model", 1_000, 2_000, 0, 0);

        var scan = new CostScanResult(tally, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, 1);
        var report = CostEstimator.Estimate(scan, PricingTable.Empty);

        Assert.Equal(0m, report.TotalCost);
        Assert.Equal(1_000, report.InputTokens);
        Assert.Equal(2_000, report.OutputTokens);
        Assert.Single(report.ModelsWithoutPricing!);
    }

    [Fact]
    public void BilinmeyenOlayZamaniRaporeTasiniyor()
    {
        var tally = new TokenTally();
        tally.Add("model", 10, 0, 0, 0);
        var scan = new CostScanResult(
            tally,
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow,
            1)
        {
            PeriodKnown = false,
        };

        var report = CostEstimator.Estimate(scan, PricingTable.Empty);

        Assert.False(report.PeriodKnown);
    }

    [Fact]
    public void LiteLlmFiyatlariTokenBasinaDonusturulurVeAliasEklenir()
    {
        var downloadedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var table = PricingTable.FromLiteLlmJson("""
            {
              "global.anthropic.claude-sonnet-4-6": {
                "input_cost_per_token": 0.000003,
                "output_cost_per_token": 0.000015,
                "cache_read_input_token_cost": 0.0000003,
                "cache_creation_input_token_cost": 0.00000375
              },
              "sample_spec": { "input_cost_per_token": "not-a-price" }
            }
            """, downloadedAt);

        var rate = table.Find("claude-sonnet-4-6-20260901");

        Assert.NotNull(rate);
        Assert.Equal(3m, rate!.InputPerMillion);
        Assert.Equal(15m, rate.OutputPerMillion);
        Assert.Equal(0.3m, rate.CacheReadPerMillion);
        Assert.Equal(3.75m, rate.CacheWritePerMillion);
        Assert.Equal(downloadedAt, table.DownloadedAt);
    }

    [Fact]
    public void ModelsDevMaliyetSemasiniOkur()
    {
        var table = PricingTable.FromModelsDevJson("""
            {
              "anthropic": {
                "models": {
                  "claude-sonnet-4-6": {
                    "id": "anthropic/claude-sonnet-4-6",
                    "cost": {
                      "input": 3.0,
                      "output": 15.0,
                      "cache_read": 0.3,
                      "cache_write": 3.75
                    }
                  }
                }
              }
            }
            """, DateTimeOffset.UtcNow);

        var rate = table.Find("claude-sonnet-4-6-20260901");

        Assert.NotNull(rate);
        Assert.Equal(3m, rate!.InputPerMillion);
        Assert.Equal(15m, rate.OutputPerMillion);
        Assert.Equal(0.3m, rate.CacheReadPerMillion);
        Assert.Equal(3.75m, rate.CacheWritePerMillion);
    }

    [Fact]
    public void FiyatZincirindeIlkKaynakKazanir()
    {
        var first = new PricingTable(new Dictionary<string, ModelRate>
        {
            ["model"] = new(1m, 2m, 0m, 0m),
        });
        var fallback = new PricingTable(new Dictionary<string, ModelRate>
        {
            ["model"] = new(9m, 9m, 0m, 0m),
            ["eksik"] = new(3m, 4m, 0m, 0m),
        });

        var merged = first.MergeMissing(fallback, out var added);

        Assert.Equal(1, added);
        Assert.Equal(1m, merged.Find("model")!.InputPerMillion);
        Assert.Equal(3m, merged.Find("eksik")!.InputPerMillion);
    }

    [Fact]
    public void ModelBazliTokenlarVeEnCokKullanilanModelTasincir()
    {
        var tally = new TokenTally();
        tally.Add("az-model", 10, 10, 0, 0);
        tally.Add("cok-model", 60, 20, 0, 0);

        var report = CostEstimator.Estimate(
            new CostScanResult(tally, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, 1),
            PricingTable.Empty);

        Assert.Equal(
            new[]
            {
                new ModelTokenUsage("cok-model", 80, 60, 20),
                new ModelTokenUsage("az-model", 20, 10, 10),
            },
            report.Models);
    }

    [Fact]
    public void UcretsizModelParaliOnekFiyatinaEslenmez()
    {
        var freeCatalogPath = Path.Combine(
            Path.GetTempPath(),
            $"ration-free-models-{Guid.NewGuid():N}.json");
        File.WriteAllText(freeCatalogPath, "{\"models\":[]}");

        try
        {
            var tally = new TokenTally();
            tally.Add(
                "muse-spark-1.3-contributor-free",
                inputTokens: 1_000_000,
                outputTokens: 1_000_000,
                cacheReadTokens: 0,
                cacheCreationTokens: 0);
            var scan = new CostScanResult(
                tally,
                DateTimeOffset.UtcNow.AddHours(-1),
                DateTimeOffset.UtcNow,
                1);
            var pricing = new PricingTable(new Dictionary<string, ModelRate>
            {
                ["muse-spark-1.3"] = new(2.30m, 2.30m, 2.30m, 2.30m),
            });

            var report = CostEstimator.Estimate(
                scan,
                pricing,
                freeModels: FreeModelCatalog.LoadOrEmpty(freeCatalogPath));

            Assert.Equal(0m, report.TotalCost);
            Assert.Empty(report.ModelsWithoutPricing!);
            Assert.Equal("muse-spark-1.3-contributor-free", Assert.Single(report.Models!).Model);
        }
        finally
        {
            try { File.Delete(freeCatalogPath); } catch (IOException) { }
        }
    }
}

public sealed class JsonlSchemaProbeTests : IDisposable
{
    private readonly string _dir;

    public JsonlSchemaProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ration-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AnahtarYollariniVerirDegerleriVermez()
    {
        File.WriteAllLines(Path.Combine(_dir, "oturum.jsonl"), new[]
        {
            """{"type":"event_msg","payload":{"type":"token_count","info":{"input_tokens":123,"gizli_metin":"bu-cikmamali"}}}""",
        });

        var paths = JsonlSchemaProbe.DescribeKeyPaths(_dir, "token_count");

        Assert.Contains("payload.info.input_tokens : Number", paths);
        Assert.Contains("payload.info.gizli_metin : String", paths);

        // Degerler asla cikmaz.
        Assert.DoesNotContain(paths, p => p.Contains("bu-cikmamali", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("123", StringComparison.Ordinal));
    }

    [Fact]
    public void FiltreyleEslesmeyenSatirlariAtlar()
    {
        File.WriteAllLines(Path.Combine(_dir, "oturum.jsonl"), new[]
        {
            """{"type":"message","icerik":{"metin":"alakasiz"}}""",
        });

        var paths = JsonlSchemaProbe.DescribeKeyPaths(_dir, "token_count");

        Assert.Empty(paths);
    }
}

public sealed class TokenTallyDailyTests
{
    [Fact]
    public void GunlukKovalar_YerelGuneGoreAyrilir_ZamaniOlmayanGrafigeGirmez()
    {
        var tally = new TokenTally();
        var day1 = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 20)));
        var day2 = day1.AddDays(1);

        tally.Add("m", 10, 5, 0, 0, at: day1);
        tally.Add("m", 1, 1, 1, 1, at: day1);
        tally.Add("m", 100, 0, 0, 0, at: day2);
        tally.Add("m", 999, 0, 0, 0);

        Assert.Equal(
            [new DailyTokens(new DateOnly(2026, 9, 20), 19), new DailyTokens(new DateOnly(2026, 9, 21), 100)],
            tally.Daily);
        Assert.Equal(1110, tally.TotalInputTokens);
    }
}
