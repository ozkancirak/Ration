using System.Text.Json;
using Ration.Core.Providers;

namespace Ration.Core.Cost;

/// <summary>
/// Totals the token_count events in ~/.codex/sessions/YYYY/MM/DD/*.jsonl.
///
/// WARNING: unlike Claude, this schema has not been verified against real data yet. Parsing
/// is tolerant and silently skips lines it does not recognize; under no circumstances does it
/// make up numbers. To see the schema: ration cost --schema
///
/// PRIVACY: only numbers and the model name are read; conversation content is never opened.
///
/// Double-count protection: an event can report either "used in this step" (last_token_usage)
/// or "session total" (total_token_usage). If total_token_usage is present, the cumulative
/// progress is taken as a delta; if only last_token_usage is present, a repeat of the same
/// report is de-duplicated.
/// </summary>
public static class CodexCostScanner
{
    public static CostScanResult Scan(
        DateTimeOffset since,
        string? sessionsDirectory = null,
        CancellationToken ct = default)
    {
        var directory = sessionsDirectory ?? KnownPaths.CodexSessionsDir;
        var tally = new TokenTally();
        var now = DateTimeOffset.UtcNow;

        if (!Directory.Exists(directory))
        {
            return new CostScanResult(tally, since, now, 0, L.T($"Directory not found: {directory}", $"Dizin bulunamadı: {directory}"));
        }

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

        // In some sessions (e.g. codex-auto-review) events carry no model name; Codex keeps the model
        // per session file in its own state database. Without this lookup the result was "(unknown model)".
        var threadModels = ReadThreadModels(Path.GetDirectoryName(Path.GetFullPath(directory)));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                filesScanned++;
                ScanFile(reader, since, tally, ref periodKnown, threadModels.GetValueOrDefault(Path.GetFullPath(file)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var note = tally.IsEmpty
            ? L.T("No token data found. Schema not verified — run 'ration cost --schema' to see the keys.", "Token verisi bulunamadı. Şema doğrulanmamış — 'ration cost --schema' ile anahtarları görebilirsiniz.")
            : L.T("Schema not yet verified against real data; double-check the numbers once.", "Şema henüz gerçek veriyle doğrulanmadı; sayıları bir kez teyit edin.");

        return new CostScanResult(tally, since, now, filesScanned, note)
        {
            PeriodKnown = periodKnown,
        };
    }

    private sealed record TokenEvent(
        string? Model,
        long[] Usage,
        DateTimeOffset? At,
        bool Cumulative,
        string? Identity);

    private static void ScanFile(
        StreamReader reader,
        DateTimeOffset since,
        TokenTally tally,
        ref bool periodKnown,
        string? fallbackModel = null)
    {
        string? currentModel = fallbackModel;
        var events = new List<TokenEvent>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object) continue;

                var model = FindModel(root);
                if (model is not null) currentModel = model;

                var info = FindTokenCountInfo(root);
                if (info is null) continue;

                // total_token_usage is cumulative. If one event has both total and last,
                // only total is used; adding the two fields would be wrong.
                var total = ReadUsage(info.Value, "total_token_usage");
                if (total is not null)
                {
                    events.Add(new TokenEvent(
                        currentModel,
                        total,
                        ReadEventTimestamp(root),
                        Cumulative: true,
                        ReadEventIdentity(root)));
                    continue;
                }

                var incremental = ReadUsage(info.Value, "last_token_usage") ?? ReadUsageDirect(info.Value);
                if (incremental is not null)
                {
                    events.Add(new TokenEvent(
                        currentModel,
                        incremental,
                        ReadEventTimestamp(root),
                        Cumulative: false,
                        ReadEventIdentity(root)));
                }
            }
            catch (JsonException) { }
        }

        if (events.Any(item => item.Cumulative))
        {
            AddCumulative(events.Where(item => item.Cumulative), since, tally, ref periodKnown);
        }
        else
        {
            AddIncremental(events, since, tally, ref periodKnown);
        }
    }

    private static void AddCumulative(
        IEnumerable<TokenEvent> events,
        DateTimeOffset since,
        TokenTally tally,
        ref bool periodKnown)
    {
        long[]? previous = null;

        // Normal Codex logs are chronological; the ordering also keeps the counter delta
        // from going backwards when a file is partially rewritten.
        foreach (var item in events.OrderBy(item => item.At ?? DateTimeOffset.MaxValue))
        {
            if (!IncludeInPeriod(item.At, since, ref periodKnown))
            {
                previous = (long[])item.Usage.Clone();
                continue;
            }

            var delta = previous is null || HasDecreased(item.Usage, previous)
                ? item.Usage
                : Subtract(item.Usage, previous);

            previous = (long[])item.Usage.Clone();
            if (Sum(delta) == 0) continue;

            tally.Add(item.Model, delta[0], delta[1], delta[2], delta[3], delta[4], item.At);
        }
    }

    private static void AddIncremental(
        IEnumerable<TokenEvent> events,
        DateTimeOffset since,
        TokenTally tally,
        ref bool periodKnown)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in events)
        {
            if (!IncludeInPeriod(item.At, since, ref periodKnown)) continue;

            // If the same JSON line/event was written twice, usage is not increased. Use the
            // event id if there is one, otherwise the provider's usage vector.
            var fingerprint = item.Identity is { Length: > 0 }
                ? $"id\0{item.Identity}"
                : $"usage\0{item.Model}\0{string.Join(',', item.Usage)}";
            if (!seen.Add(fingerprint)) continue;

            tally.Add(item.Model, item.Usage[0], item.Usage[1], item.Usage[2], item.Usage[3], item.Usage[4], item.At);
        }
    }

    private static bool IncludeInPeriod(
        DateTimeOffset? at,
        DateTimeOffset since,
        ref bool periodKnown)
    {
        if (at is null)
        {
            periodKnown = false;
            return true;
        }

        return at >= since;
    }

    private static bool HasDecreased(long[] current, long[] previous) =>
        current[0] < previous[0] ||
        current[1] < previous[1] ||
        current[2] < previous[2] ||
        current[3] < previous[3] ||
        current[4] < previous[4];

    private static long[] Subtract(long[] current, long[] previous) =>
        new[]
        {
            Math.Max(0, current[0] - previous[0]),
            Math.Max(0, current[1] - previous[1]),
            Math.Max(0, current[2] - previous[2]),
            Math.Max(0, current[3] - previous[3]),
            Math.Max(0, current[4] - previous[4]),
        };

    // Reasoning is a subset, so it is not included in the size comparison.
    private static long Sum(long[] usage) => usage[0] + usage[1] + usage[2] + usage[3];

    /// <summary>Finds the info object of a token_count event; null if there is none.</summary>
    private static JsonElement? FindTokenCountInfo(JsonElement root)
    {
        // Format 1: { type: "event_msg", payload: { type: "token_count", info: {...} } }
        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object &&
            IsTokenCount(payload))
        {
            return payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                ? info
                : payload;
        }

        // Format 2: { type: "token_count", info: {...} }
        if (IsTokenCount(root))
        {
            return root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                ? info
                : root;
        }

        return null;
    }

    private static bool IsTokenCount(JsonElement element) =>
        element.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), "token_count", StringComparison.OrdinalIgnoreCase);

    private static long[]? ReadUsage(JsonElement parent, string childName) =>
        parent.TryGetProperty(childName, out var child) && child.ValueKind == JsonValueKind.Object
            ? ReadUsageDirect(child)
            : null;

    private static long[]? ReadUsageDirect(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var rawInput = ReadLong(element, "input_tokens");
        var output = ReadLong(element, "output_tokens");
        var cacheRead = Math.Max(
            ReadLong(element, "cached_input_tokens"),
            ReadLong(element, "cache_read_input_tokens"));

        // Codex input_tokens is the total input including cache. To avoid counting the
        // cache twice, split it out into normal input at the provider boundary.
        var input = Math.Max(0, rawInput - cacheRead);

        // Codex says "cache_write_input_tokens". The second name exists only for tolerant
        // reading; if both names carry the same value, do not add them twice.
        var cacheWrite = Math.Max(
            ReadLong(element, "cache_write_input_tokens"),
            ReadLong(element, "cache_creation_input_tokens"));

        // reasoning_output_tokens is a SUBSET of output_tokens; it is tracked separately
        // and not added to the total (adding it would count the output twice).
        var reasoning = ReadLong(element, "reasoning_output_tokens");

        // If there is no token field, this is not a usage object.
        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0) return null;

        return new[] { input, output, cacheRead, cacheWrite, reasoning };
    }

    private static DateTimeOffset? ReadEventTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var timestamp) &&
            ReadTimestamp(timestamp) is { } at)
        {
            return at;
        }

        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("timestamp", out var payloadTimestamp))
        {
            return ReadTimestamp(payloadTimestamp);
        }

        return null;
    }

    private static string? ReadEventIdentity(JsonElement root)
    {
        foreach (var name in new[] { "id", "event_id", "message_id", "response_id" })
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "id", "event_id", "message_id", "response_id" })
            {
                if (payload.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }

        return null;
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

    /// <summary>
    /// Session file → model mapping from Codex's state database (~/.codex/state_N.sqlite, threads table).
    /// Read-only; only the rollout_path and model columns are read, conversation
    /// content is never touched. Returns empty if the database is missing or its schema changed.
    /// </summary>
    private static Dictionary<string, string> ReadThreadModels(string? codexHome)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (codexHome is null || !Directory.Exists(codexHome)) return result;

        var database = Directory.EnumerateFiles(codexHome, "state_*.sqlite")
            .OrderByDescending(path => int.TryParse(
                Path.GetFileNameWithoutExtension(path)["state_".Length..], out var version) ? version : -1)
            .FirstOrDefault();
        if (database is null) return result;

        try
        {
            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rollout_path, model FROM threads WHERE model IS NOT NULL AND rollout_path IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[Path.GetFullPath(reader.GetString(0))] = reader.GetString(1);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // If Codex changes its schema, only this fallback is lost; the scan continues.
        }

        return result;
    }

    private static string? FindModel(JsonElement root)
    {
        if (root.TryGetProperty("turn_context", out var turnContext) &&
            turnContext.ValueKind == JsonValueKind.Object)
        {
            var fromContext = ReadString(turnContext, "model");
            if (fromContext is not null) return fromContext;
        }

        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object)
        {
            var fromPayload = ReadString(payload, "model");
            if (fromPayload is not null) return fromPayload;
        }

        return ReadString(root, "model");
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
