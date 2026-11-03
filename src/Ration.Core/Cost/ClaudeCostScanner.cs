using System.Text.Json;
using Ration.Core.Providers;

namespace Ration.Core.Cost;

/// <summary>
/// Totals the token counts of assistant replies in ~/.claude/projects/**/*.jsonl.
///
/// PRIVACY: this scanner only reads counts and ids. Conversation content
/// (message.content) is never opened, written anywhere or included in any output.
///
/// Claude Code may be writing the files while we read them, so they are read
/// with FileShare.ReadWrite and streamed line by line (never loaded into memory whole).
/// </summary>
public static class ClaudeCostScanner
{
    public static CostScanResult Scan(
        DateTimeOffset since,
        string? projectsDirectory = null,
        CancellationToken ct = default)
    {
        var directory = projectsDirectory ?? KnownPaths.ClaudeProjectsDir;
        var tally = new TokenTally();
        var now = DateTimeOffset.UtcNow;

        if (!Directory.Exists(directory))
        {
            return new CostScanResult(tally, since, now, 0, L.T($"Directory not found: {directory}", $"Dizin bulunamadı: {directory}"));
        }

        // The same reply can appear on several lines (rewrite, resume record).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var filesScanned = 0;
        var periodKnown = true;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            return new CostScanResult(tally, since, now, 0, L.T("Directory could not be read (permission).", "Dizin okunamadı (yetki)."));
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                filesScanned++;

                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0) continue;

                    ReadLine(line, since, seen, tally, ref periodKnown);
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

    private static void ReadLine(
        string line,
        DateTimeOffset since,
        HashSet<string> seen,
        TokenTally tally,
        ref bool periodKnown)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return;

            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!message.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // The period filter uses the event time. If there is no timestamp,
            // do not lose the count, but do not present the result as an exact calendar period.
            DateTimeOffset? timestamp = null;
            if (root.TryGetProperty("timestamp", out var timestampElement))
            {
                timestamp = ReadTimestamp(timestampElement);
            }

            if (timestamp is null) periodKnown = false;
            else if (timestamp < since) return;

            // De-duplication: message.id + requestId
            var messageId = ReadString(message, "id");
            var requestId = ReadString(root, "requestId") ?? ReadString(root, "request_id");
            var key = $"{messageId}|{requestId}";

            if (messageId is not null && !seen.Add(key)) return;

            // output_tokens_details.thinking_tokens is a SUBSET of output_tokens
            // (the same pattern as reasoning_output_tokens in Codex): it is tracked
            // separately and not added to the total.
            var thinking = 0L;
            if (usage.TryGetProperty("output_tokens_details", out var details) &&
                details.ValueKind == JsonValueKind.Object)
            {
                thinking = ReadLong(details, "thinking_tokens") + ReadLong(details, "reasoning_tokens");
            }

            tally.Add(
                ReadString(message, "model"),
                ReadLong(usage, "input_tokens"),
                ReadLong(usage, "output_tokens"),
                ReadLong(usage, "cache_read_input_tokens"),
                ReadLong(usage, "cache_creation_input_tokens"),
                thinking,
                timestamp);
        }
        catch (JsonException)
        {
            // A half-written last line is normal; skip it silently.
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var epoch))
        {
            return epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0L;
}
