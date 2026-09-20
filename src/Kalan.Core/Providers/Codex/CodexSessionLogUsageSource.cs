using System.Text.Json;
using Kalan.Core.Abstractions;
using Kalan.Core.Model;

namespace Kalan.Core.Providers.Codex;

/// <summary>
/// Ağsız ikinci kaynak: ~/.codex/sessions/**/*.jsonl içindeki rate_limits payload'ı.
/// CodexProvider zincirinde OAuth'tan SONRA gelir; ağ başarısız olunca devreye girer.
///
/// GİZLİLİK (AGENTS.md §2.3): satırlardan YALNIZCA payload.rate_limits (yüzdeler,
/// pencere süreleri) ve zaman damgası okunur. Mesaj metni, prompt, araç çıktısı
/// hiçbir şekilde ayrıştırılmaz, saklanmaz, loglanmaz.
/// Dosyalar Codex CLI tarafından yazılıyor olabilir: FileShare.ReadWrite ile açılır,
/// satır satır akıtılır, kilit tutulmaz (AGENTS.md §2.1).
/// </summary>
public sealed class CodexSessionLogUsageSource : IUsageSource
{
    /// <summary>Çok eski dosyalara inmeden dur; tazelik zaten StaleReason'da görünür.</summary>
    private const int MaxFilesScanned = 50;

    private readonly string _sessionsDir;

    public SourceKind Kind => SourceKind.LocalFile;

    public CodexSessionLogUsageSource(string? sessionsDir = null)
    {
        _sessionsDir = sessionsDir ?? KnownPaths.CodexSessionsDir;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(Directory.Exists(_sessionsDir));

    public Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult(Scan(ct));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Snapshot.Empty("codex", ProviderStatus.Error,
                $"Oturum logu okunamadı: {ex.GetType().Name}", Kind));
        }
    }

    private UsageSnapshot Scan(CancellationToken ct)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(_sessionsDir, "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(MaxFilesScanned);
        }
        catch (UnauthorizedAccessException)
        {
            return Snapshot.Empty("codex", ProviderStatus.Error,
                "Oturum dizini okunamadı (yetki).", Kind);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // İleriden tarayıp SON geçerli satırı tutmak, sondan geriye tarayıp
            // İLKİ almaya denktir; tek geçişle O(1) bellekte yapılır.
            if (TryReadLatest(file) is { } hit)
            {
                var age = FormatAge(DateTimeOffset.UtcNow - hit.At);
                return new UsageSnapshot(
                    ProviderId: "codex",
                    Windows: hit.Data.Windows,
                    Credits: hit.Data.Credits,
                    Cost: null,
                    Status: hit.Data.Windows.Count > 0 ? ProviderStatus.Ok : ProviderStatus.Degraded,
                    ResolvedVia: Kind,
                    FetchedAt: DateTimeOffset.UtcNow,
                    StaleReason: hit.Data.Windows.Count > 0
                        ? $"oturum kaydından · {age}"
                        : "Oturum loglarında rate_limits bulunamadı.",
                    PlanName: hit.Data.PlanName);
            }
        }

        return Snapshot.Empty("codex", ProviderStatus.Degraded,
            "Oturum loglarında rate_limits bulunamadı.", Kind);
    }

    private sealed record Hit(CodexUsageData Data, DateTimeOffset At);

    private static Hit? TryReadLatest(string file)
    {
        CodexUsageData? best = null;
        DateTimeOffset bestAt = default;
        DateTimeOffset fileTime;
        try
        {
            fileTime = File.GetLastWriteTimeUtc(file);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (CodexSessionLogParser.TryParseLine(doc.RootElement, fileTime, out var data, out var at) &&
                        data is not null)
                    {
                        best = data;
                        bestAt = at;
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }

        return best is null ? null : new Hit(best, bestAt);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1) return "az önce";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} dk önce";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} sa önce";
        return $"{(int)age.TotalDays} gün önce";
    }
}

/// <summary>
/// Oturum satırındaki rate_limits zarfını ayrıştırır. Şema wham/usage yanıtından
/// farklıdır (primary/window_minutes/resets_at), bu yüzden ayrı okunur; pencere
/// adı/türü türetme (<see cref="CodexUsageParser"/>) aynen yeniden kullanılır.
/// </summary>
public static class CodexSessionLogParser
{
    /// <summary>Satırda kullanılabilir rate_limits varsa true + veri ve satır zamanı.</summary>
    public static bool TryParseLine(
        JsonElement root,
        DateTimeOffset fileTime,
        out CodexUsageData? data,
        out DateTimeOffset at)
    {
        data = null;
        at = fileTime;

        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (!payload.TryGetProperty("rate_limits", out var limits) ||
            limits.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Satır zamanı: önce satırdaki timestamp, yoksa dosya mtime'ı.
        if (root.TryGetProperty("timestamp", out var ts) &&
            JsonHelpers.ReadTimestamp(ts) is { } parsed)
        {
            at = parsed;
        }

        data = ParseRateLimits(limits);
        return true;
    }

    public static CodexUsageData ParseRateLimits(JsonElement limits)
    {
        var windows = new List<UsageWindow>();

        TryAddWindow(limits, "primary", WindowKind.Session, windows);
        TryAddWindow(limits, "secondary", WindowKind.Weekly, windows);

        return new CodexUsageData(
            windows,
            CodexUsageParser.ReadString(limits, "plan_type"),
            CodexUsageParser.ReadCredits(limits));
    }

    private static void TryAddWindow(
        JsonElement parent,
        string key,
        WindowKind fallbackKind,
        List<UsageWindow> into)
    {
        if (!parent.TryGetProperty(key, out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var percent = JsonHelpers.ReadFirstNumber(element, "used_percent");
        if (percent is null) return;

        // Oturum zarfı süreyi dakika verir (wham saniye verirdi).
        var minutes = JsonHelpers.ReadFirstNumber(element, "window_minutes");
        double? windowSeconds = minutes is > 0 ? minutes.Value * 60 : null;

        DateTimeOffset? resetsAt = null;
        if (element.TryGetProperty("resets_at", out var resetElement))
        {
            resetsAt = JsonHelpers.ReadTimestamp(resetElement);
        }

        var clamped = Math.Clamp(percent.Value, 0, 100);
        into.Add(new UsageWindow(
            CodexUsageParser.KindFromSeconds(windowSeconds, fallbackKind),
            clamped, 100, clamped,
            resetsAt,
            CodexUsageParser.DescribeWindow(windowSeconds, fallbackKind),
            windowSeconds is > 0 ? TimeSpan.FromSeconds(windowSeconds.Value) : null));
    }
}
