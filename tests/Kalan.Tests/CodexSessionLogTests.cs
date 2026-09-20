using Kalan.Core.Abstractions;
using Kalan.Core.Model;
using Kalan.Core.Providers.Codex;

namespace Kalan.Tests;

/// <summary>
/// Oturum log kaynağı testleri. Tüm JSONL'lar SENTETİKTİR, temp dizinde üretilir;
/// gerçek ~/.codex/sessions içeriği repoya giremez (AGENTS.md §2.3).
/// </summary>
public class CodexSessionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kalan-sess-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteSession(string name, params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private const string RateTemplate = """
        {"timestamp":"2026-09-20T10:00:00Z","type":"event_msg","payload":{"rate_limits":{"primary":{"used_percent":__P__,"window_minutes":300,"resets_at":1789838400},"secondary":{"used_percent":__S__,"window_minutes":10080,"resets_at":1789838400},"plan_type":"plus","credits":{"balance":"0"}}}}
        """;

    private static string RateLine(double primary, double secondary) => RateTemplate
        .Replace("__P__", primary.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("__S__", secondary.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task OturumLogu_PencereleriOkur()
    {
        WriteSession("a.jsonl", RateLine(31.5, 7.5));

        var source = new CodexSessionLogUsageSource(_dir);
        Assert.True(await source.IsAvailableAsync());

        var snapshot = await source.FetchAsync();

        Assert.Equal("codex", snapshot.ProviderId);
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.LocalFile, snapshot.ResolvedVia);

        var session = Assert.Single(snapshot.Windows, w => w.Kind == WindowKind.Session);
        Assert.Equal(31.5, session.Percent);
        Assert.Equal(TimeSpan.FromHours(5), session.WindowLength);

        var weekly = Assert.Single(snapshot.Windows, w => w.Kind == WindowKind.Weekly);
        Assert.Equal(7.5, weekly.Percent);
        Assert.Equal(TimeSpan.FromDays(7), weekly.WindowLength);

        Assert.Equal("plus", snapshot.PlanName);
        Assert.Contains("oturum kaydından", snapshot.StaleReason);
    }

    [Fact]
    public async Task EnYeniDosya_Ve_SonSatir_Kazanir()
    {
        var oldPath = WriteSession("old.jsonl", RateLine(10, 10));
        var newPath = WriteSession("new.jsonl", RateLine(20, 20), RateLine(42, 43));
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddHours(-5));
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);

        var snapshot = await new CodexSessionLogUsageSource(_dir).FetchAsync();

        // Aynı dosyada son satır (42), dosyalar arasında en yeni dosya kazanır.
        Assert.Equal(42, Assert.Single(snapshot.Windows, w => w.Kind == WindowKind.Session).Percent);
    }

    [Fact]
    public async Task ZamanDamgasi_Yoksa_DosyaZamani_Kullanilir()
    {
        const string line = """
            {"type":"response","payload":{"rate_limits":{"primary":{"used_percent":11,"window_minutes":300},"secondary":{"used_percent":12,"window_minutes":10080}}}}
            """;
        var path = WriteSession("b.jsonl", line);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-12));

        var snapshot = await new CodexSessionLogUsageSource(_dir).FetchAsync();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Contains("oturum kaydından · 12 dk önce", snapshot.StaleReason);
    }

    [Fact]
    public async Task RateLimits_Yoksa_DegradedDoner()
    {
        WriteSession("c.jsonl", """{"type":"response","payload":{"info":{}}}""");

        var snapshot = await new CodexSessionLogUsageSource(_dir).FetchAsync();

        Assert.Equal(ProviderStatus.Degraded, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task OlmayanDizin_Kullanilamaz()
    {
        var source = new CodexSessionLogUsageSource(Path.Combine(Path.GetTempPath(), $"kalan-yok-{Guid.NewGuid():N}"));

        Assert.False(await source.IsAvailableAsync());
    }

    [Fact]
    public void Zincirde_OAuthTanSonra_Gelir()
    {
        using var http = new HttpClient();
        var provider = new CodexProvider(http);

        Assert.Equal(2, provider.Sources.Count);
        Assert.IsType<CodexOAuthUsageSource>(provider.Sources[0]);
        Assert.IsType<CodexSessionLogUsageSource>(provider.Sources[1]);
    }
}
