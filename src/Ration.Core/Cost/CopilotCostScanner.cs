using System.Text.Json;
using System.Text.RegularExpressions;
using Ration.Core.Providers;

namespace Ration.Core.Cost;

/// <summary>
/// Totals the token usage of GitHub Copilot CLI sessions from ${COPILOT_HOME:-~/.copilot}/session-state/*/events.jsonl.
/// EXPERIMENTAL: the format follows the documentation of other tools and has not been checked against a real install.
///
/// Only session.shutdown events carry usage: per model, cumulative for the session. A resumed session writes
/// another shutdown, so each snapshot counts as the difference from the one before it (the first as it is),
/// and the usage lands on the day of the snapshot. Snapshots older than the period still serve as the baseline.
///
/// PRIVACY: only counts, model names and times are read. The other lines of the file hold the conversation;
/// they are skipped without being parsed.
/// </summary>
public static partial class CopilotCostScanner
{
    private readonly record struct Usage(long Input, long Output, long CacheRead, long CacheWrite, long Reasoning)
    {
        public bool IsZero => Input == 0 && Output == 0 && CacheRead == 0 && CacheWrite == 0 && Reasoning == 0;

        // A counter that went down means the totals restarted; the snapshot then counts as it is.
        public bool WentDownFrom(Usage before) =>
            Input < before.Input || Output < before.Output || CacheRead < before.CacheRead ||
            CacheWrite < before.CacheWrite || Reasoning < before.Reasoning;

        public Usage Minus(Usage before) =>
            new(Input - before.Input, Output - before.Output, CacheRead - before.CacheRead, CacheWrite - before.CacheWrite, Reasoning - before.Reasoning);
    }

    /// <summary>
    /// true once at least one session has left an events.jsonl. A session-state folder that exists but is empty
    /// (the CLI was installed and never used) has nothing to show.
    /// </summary>
    public static bool HasSessionLogs(string? sessionStateDirectory = null)
    {
        var directory = sessionStateDirectory ?? KnownPaths.CopilotSessionStateDir;
        try
        {
            return Directory.Exists(directory) &&
                   Directory.EnumerateFiles(directory, "events.jsonl", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static CostScanResult Scan(
        DateTimeOffset since,
        string? sessionStateDirectory = null,
        CancellationToken ct = default)
    {
        var directory = sessionStateDirectory ?? KnownPaths.CopilotSessionStateDir;
        var tally = new TokenTally();
        var now = DateTimeOffset.UtcNow;

        if (!Directory.Exists(directory))
        {
            return new CostScanResult(tally, since, now, 0, L.T($"Directory not found: {directory}", $"Dizin bulunamadı: {directory}"));
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "events.jsonl", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            return new CostScanResult(tally, since, now, 0, L.T("Directory could not be read (permission).", "Dizin okunamadı (yetki)."));
        }

        var filesScanned = 0;
        var periodKnown = true;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Copilot may still be writing: open with FileShare.ReadWrite, stream line by line.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                filesScanned++;

                // One file is one session: the baselines are per model.
                var previous = new Dictionary<string, Usage>(StringComparer.OrdinalIgnoreCase);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0 || !line.Contains("\"session.shutdown\"", StringComparison.Ordinal)) continue;

                    ReadShutdown(line, since, previous, tally, ref periodKnown);
                }
            }
            catch (IOException) { /* file is locked or deleted: skip */ }
            catch (UnauthorizedAccessException) { /* skip */ }
        }

        return new CostScanResult(tally, since, now, filesScanned)
        {
            PeriodKnown = periodKnown,
        };
    }

    private static void ReadShutdown(
        string line,
        DateTimeOffset since,
        Dictionary<string, Usage> previous,
        TokenTally tally,
        ref bool periodKnown)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                JsonHelpers.ReadString(root, "type") != "session.shutdown" ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("modelMetrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            DateTimeOffset? timestamp = root.TryGetProperty("timestamp", out var timestampElement)
                ? JsonHelpers.ReadTimestamp(timestampElement)
                : null;

            foreach (var entry in metrics.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object ||
                    !entry.Value.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var current = new Usage(
                    ReadLong(usage, "inputTokens"),
                    ReadLong(usage, "outputTokens"),
                    ReadLong(usage, "cacheReadTokens"),
                    ReadLong(usage, "cacheWriteTokens"),
                    ReadLong(usage, "reasoningTokens"));

                var interval = previous.TryGetValue(entry.Name, out var before) && !current.WentDownFrom(before)
                    ? current.Minus(before)
                    : current;
                previous[entry.Name] = current;

                // The period uses the snapshot time; without one the count is kept, but the result is not an exact period.
                if (timestamp is null) periodKnown = false;
                else if (timestamp < since) continue;

                if (interval.IsZero) continue;

                // inputTokens includes both cache buckets; reasoning is a subset of the output.
                var uncachedInput = Math.Max(interval.Input - interval.CacheRead - interval.CacheWrite, 0);
                tally.Add(
                    NormalizeModel(entry.Name),
                    uncachedInput,
                    interval.Output,
                    interval.CacheRead,
                    interval.CacheWrite,
                    interval.Reasoning,
                    timestamp);
            }
        }
        catch (JsonException)
        {
            // A half-written last line is normal; skip it silently.
        }
    }

    /// <summary>
    /// Copilot names models its own way: "-1m" context suffixes and dotted Claude versions
    /// ("claude-opus-4.7") that the price list spells "claude-opus-4-7".
    /// </summary>
    public static string NormalizeModel(string model)
    {
        var name = model.Trim();
        foreach (var suffix in new[] { "-1m-internal", "-1m" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        }

        return name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? VersionDot().Replace(name, "-") : name;
    }

    [GeneratedRegex(@"(?<=\d)\.(?=\d)")]
    private static partial Regex VersionDot();

    private static long ReadLong(JsonElement element, string name) =>
        JsonHelpers.ReadFirstNumber(element, name) is { } value && value > 0 ? (long)value : 0;
}
