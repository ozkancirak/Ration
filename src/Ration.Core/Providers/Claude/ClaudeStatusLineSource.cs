using System.Globalization;
using System.Text.Json;
using Ration.Core.Abstractions;
using Ration.Core.Model;

namespace Ration.Core.Providers.Claude;

/// <summary>
/// Claude Code statusLine köprüsü. Claude Code her durum satırı yenilemesinde oturum
/// JSON'unu komutun stdin'ine verir; içinde <c>rate_limits.five_hour</c> ve
/// <c>rate_limits.seven_day</c> ({ used_percentage, resets_at }) bulunur.
///
/// Neden: OAuth kota uç noktası token süresi dolunca 401, sık sorguda 429 döner.
/// statusLine verisi ağ ve token gerektirmez, hız sınırına takılmaz.
///
/// GİZLİLİK: stdin'de konuşma bağlamı, çalışma dizini ve maliyet de vardır. Yalnızca
/// dört sayı diske yazılır; geri kalan hiçbir alan okunmaz ya da saklanmaz.
/// Kullanıcının ~/.claude/settings.json dosyasına Ration yazmaz;
/// statusLine'ı kullanıcı kendisi bağlar.
/// </summary>
public static class ClaudeStatusLine
{
    public static string DefaultPath => Path.Combine(KnownPaths.CacheDir, "claude-statusline.json");

    public sealed record Limits(double FiveHour, DateTimeOffset? FiveHourResetsAt, double SevenDay, DateTimeOffset? SevenDayResetsAt);

    /// <summary>stdin JSON'undan kota alanlarını okur; eksikse null (Claude Code bazen boş gönderir).</summary>
    public static Limits? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rate_limits", out var limits) ||
                limits.ValueKind != JsonValueKind.Object) return null;

            var five = ReadWindow(limits, "five_hour");
            var seven = ReadWindow(limits, "seven_day");
            if (five is null || seven is null) return null;

            return new Limits(five.Value.Percent, five.Value.ResetsAt, seven.Value.Percent, seven.Value.ResetsAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Kota alanları tamsa atomik olarak diske yazar. Eksik veri önceki tam kaydı ezmez.</summary>
    public static Limits? Capture(string json, string? path = null, DateTimeOffset? now = null)
    {
        var limits = Parse(json);
        if (limits is null) return null;

        path ??= DefaultPath;
        var record = new Dictionary<string, object?>
        {
            ["captured_at"] = (now ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
            ["five_hour"] = new { used_percentage = limits.FiveHour, resets_at = limits.FiveHourResetsAt?.ToUnixTimeSeconds() },
            ["seven_day"] = new { used_percentage = limits.SevenDay, resets_at = limits.SevenDayResetsAt?.ToUnixTimeSeconds() },
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record));
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return limits;
    }

    /// <summary>Diskteki son kayıt ve yakalanma zamanı.</summary>
    public static (Limits Limits, DateTimeOffset CapturedAt)? Read(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("captured_at", out var at) ||
                !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var capturedAt))
            {
                return null;
            }

            var five = ReadWindow(doc.RootElement, "five_hour");
            var seven = ReadWindow(doc.RootElement, "seven_day");
            if (five is null || seven is null) return null;

            return (new Limits(five.Value.Percent, five.Value.ResetsAt, seven.Value.Percent, seven.Value.ResetsAt), capturedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<UsageWindow> ToWindows(Limits limits) => new[]
    {
        new UsageWindow(WindowKind.Session, limits.FiveHour, 100, limits.FiveHour, limits.FiveHourResetsAt, L.T("5-hour", "5 saatlik"), TimeSpan.FromHours(5)),
        new UsageWindow(WindowKind.Weekly, limits.SevenDay, 100, limits.SevenDay, limits.SevenDayResetsAt, L.T("Weekly", "Haftalık"), TimeSpan.FromDays(7)),
    };

    private static (double Percent, DateTimeOffset? ResetsAt)? ReadWindow(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return null;
        if (!window.TryGetProperty("used_percentage", out var pct) || pct.ValueKind != JsonValueKind.Number) return null;

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var reset))
        {
            // Unix saniyesi ondalıklı da gelebilir (1790157600.5); ReadTimestamp yalnızca tam sayı okur.
            resetsAt = reset.ValueKind == JsonValueKind.Number && !reset.TryGetInt64(out _)
                ? DateTimeOffset.FromUnixTimeSeconds((long)reset.GetDouble())
                : JsonHelpers.ReadTimestamp(reset);
        }
        return (Math.Clamp(pct.GetDouble(), 0, 100), resetsAt);
    }
}

/// <summary>
/// statusLine köprüsünün yazdığı dosyadan Claude kotası. OAuth'tan sonra denenir:
/// taze kayıt (Claude Code açıkken) Ok, eskisi bayatlık notuyla Degraded döner.
/// </summary>
public sealed class ClaudeStatusLineUsageSource : IUsageSource
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);
    private readonly string? _path;

    public ClaudeStatusLineUsageSource(string? path = null) => _path = path;

    public SourceKind Kind => SourceKind.LocalFile;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(File.Exists(_path ?? ClaudeStatusLine.DefaultPath));

    public Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        if (ClaudeStatusLine.Read(_path) is not { } record)
        {
            return Task.FromResult(Snapshot.Empty("claude", ProviderStatus.Error,
                L.T("Could not read the Claude Code statusLine record.", "Claude Code statusLine kaydı okunamadı."), Kind));
        }

        var fresh = DateTimeOffset.UtcNow - record.CapturedAt < FreshFor;
        return Task.FromResult(new UsageSnapshot(
            ProviderId: "claude",
            Windows: ClaudeStatusLine.ToWindows(record.Limits),
            Credits: null,
            Cost: null,
            Status: fresh ? ProviderStatus.Ok : ProviderStatus.Degraded,
            ResolvedVia: Kind,
            FetchedAt: record.CapturedAt,
            StaleReason: fresh ? null : "Claude Code statusLine verisi"));
    }
}
