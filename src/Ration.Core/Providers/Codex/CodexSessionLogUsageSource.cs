using System.Text.Json;
using Ration.Core.Abstractions;
using Ration.Core.Model;

namespace Ration.Core.Providers.Codex;

/// <summary>
/// Second source without network: the rate_limits payload in ~/.codex/sessions/**/*.jsonl.
/// It comes AFTER OAuth in the CodexProvider chain; it kicks in when the network fails.
///
/// PRIVACY: from the lines ONLY payload.rate_limits (percentages,
/// window durations) and the timestamp are read. Message text, prompts and tool output
/// are never parsed, stored or logged.
/// The files may be being written by the Codex CLI: they are opened with FileShare.ReadWrite,
/// streamed line by line and no lock is held.
/// </summary>
public sealed class CodexSessionLogUsageSource : IUsageSource
{
    /// <summary>Stop before reaching very old files; freshness is already visible in StaleReason.</summary>
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
                L.T($"Session log could not be read: {ex.GetType().Name}", $"Oturum logu okunamadı: {ex.GetType().Name}"), Kind));
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
                L.T("Session directory could not be read (permission).", "Oturum dizini okunamadı (yetki)."), Kind);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Scanning forward and keeping the LAST valid line is equivalent to scanning backward
            // from the end and taking the FIRST one; it is done in a single pass with O(1) memory.
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
                    // This source is not a network response; keep the time of the measurement. Using the time
                    // of reading as FetchedAt would make an old quota look freshly fetched.
                    FetchedAt: hit.At,
                    StaleReason: hit.Data.Windows.Count > 0
                        ? L.T($"from session log · {age}", $"oturum kaydından · {age}")
                        : L.T("No rate_limits found in session logs.", "Oturum loglarında rate_limits bulunamadı."),
                    PlanName: hit.Data.PlanName);
            }
        }

        return Snapshot.Empty("codex", ProviderStatus.Degraded,
            L.T("No rate_limits found in session logs.", "Oturum loglarında rate_limits bulunamadı."), Kind);
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
        if (age.TotalMinutes < 1) return L.T("just now", "az önce");
        if (age.TotalHours < 1) return L.T($"{(int)age.TotalMinutes} min ago", $"{(int)age.TotalMinutes} dk önce");
        if (age.TotalDays < 1) return L.T($"{(int)age.TotalHours} h ago", $"{(int)age.TotalHours} sa önce");
        return L.T($"{(int)age.TotalDays} d ago", $"{(int)age.TotalDays} gün önce");
    }
}

/// <summary>
/// Parses the rate_limits envelope in a session line. The schema differs from the wham/usage
/// response (primary/window_minutes/resets_at), so it is read separately; the window
/// name/kind derivation (<see cref="CodexUsageParser"/>) is reused as it is.
/// </summary>
public static class CodexSessionLogParser
{
    /// <summary>true plus the data and the line time if the line has usable rate_limits.</summary>
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

        // Line time: the timestamp in the line first, otherwise the file mtime.
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

        // The session envelope gives the duration in minutes (wham gave seconds).
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
