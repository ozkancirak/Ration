using Ration.Core.Cost;

namespace Ration.Tests;

/// <summary>Aynı model oranı her taramada günlüğe yeniden yazılmaz.</summary>
public sealed class PricingLogTests
{
    private static int CountLines(string needle)
    {
        var path = Environment.GetEnvironmentVariable("RATION_LOG_PATH")!;
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Count(line => line.Contains(needle, StringComparison.Ordinal));
    }

    private static CostScanResult ScanOf(string model)
    {
        var tally = new TokenTally();
        tally.Add(model, 1_000_000, 1_000_000, 0, 0);
        return new CostScanResult(tally, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, 1);
    }

    [Fact]
    public void AyniOran_TekSatirYazilir_DegisinceYenidenYazilir()
    {
        var model = $"gunluk-testi-{Guid.NewGuid():N}";
        var needle = $"model-rate model={model} ";

        var pricing = new PricingTable(new Dictionary<string, ModelRate> { [model] = new(3m, 15m, 0m, 0m) });
        for (var i = 0; i < 5; i++) CostEstimator.Estimate(ScanOf(model), pricing);

        Assert.Equal(1, CountLines(needle));

        // Oran değişti: yeni bilgi, yeniden yazılır.
        var repriced = new PricingTable(new Dictionary<string, ModelRate> { [model] = new(4m, 15m, 0m, 0m) });
        CostEstimator.Estimate(ScanOf(model), repriced);
        CostEstimator.Estimate(ScanOf(model), repriced);

        Assert.Equal(2, CountLines(needle));
    }
}
