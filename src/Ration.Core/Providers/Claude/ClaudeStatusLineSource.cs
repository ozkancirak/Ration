using System.Globalization;
using System.Text.Json;
using Ration.Core.Abstractions;
using Ration.Core.Model;

namespace Ration.Core.Providers.Claude;

/// <summary>
/// Claude Code statusLine bridge. On every status line refresh, Claude Code passes the session
/// JSON to the command's stdin; it contains <c>rate_limits.five_hour</c> and
/// <c>rate_limits.seven_day</c> ({ used_percentage, resets_at }).
///
/// Why: the OAuth quota endpoint returns 401 when the token expires and 429 on frequent queries.
/// The statusLine data needs no network or token and is not subject to the rate limit.
///
/// PRIVACY: stdin also contains the conversation context, working directory and cost. Only
/// four numbers are written to disk; no other field is read or stored.
/// Ration does not write to the user's ~/.claude/settings.json;
/// the user wires up the statusLine themselves.
/// </summary>
public static class ClaudeStatusLine
{
    public static string DefaultPath => Path.Combine(KnownPaths.CacheDir, "claude-statusline.json");

    public sealed record Limits(double FiveHour, DateTimeOffset? FiveHourResetsAt, double SevenDay, DateTimeOffset? SevenDayResetsAt);

    /// <summary>Reads the quota fields from the stdin JSON; null if they are missing (Claude Code sometimes sends it empty).</summary>
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

    /// <summary>Writes atomically to disk if the quota fields are complete. Incomplete data does not overwrite the previous complete record.</summary>
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

    /// <summary>The last record on disk and the time it was captured.</summary>
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
            // Unix seconds can also arrive with a fraction (1790157600.5); ReadTimestamp only reads whole numbers.
            resetsAt = reset.ValueKind == JsonValueKind.Number && !reset.TryGetInt64(out _)
                ? DateTimeOffset.FromUnixTimeSeconds((long)reset.GetDouble())
                : JsonHelpers.ReadTimestamp(reset);
        }
        return (Math.Clamp(pct.GetDouble(), 0, 100), resetsAt);
    }
}

/// <summary>
/// Claude quota from the file written by the statusLine bridge. Tried after OAuth:
/// a fresh record (while Claude Code is open) returns Ok, an older one returns Degraded with a staleness note.
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
