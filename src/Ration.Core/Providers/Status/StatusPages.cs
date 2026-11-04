using System.Text.Json;

namespace Ration.Core.Providers.Status;

public enum OutageLevel
{
    None,
    Degraded,
    Outage,
}

/// <param name="Detail">What is affected, in one short line; null when everything is fine.</param>
public sealed record StatusReport(OutageLevel Level, string? Detail);

/// <summary>A provider's public Statuspage feed and the components that matter for its quota and coding tools.</summary>
public sealed record StatusPageSource(string SummaryUrl, IReadOnlyList<string> Components);

public static class ProviderStatusPages
{
    /// <summary>Null for providers without a Statuspage feed (Antigravity, OpenCode).</summary>
    public static StatusPageSource? For(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => new("https://status.claude.com/api/v2/summary.json", ["Claude Code", "Claude API (api.anthropic.com)"]),
        "codex" => new("https://status.openai.com/api/v2/summary.json", ["Codex in ChatGPT Desktop", "Responses"]),
        "cursor" => new("https://status.cursor.com/api/v2/summary.json", ["IDE", "CLI", "cursor.com"]),
        _ => null,
    };
}

/// <summary>
/// Reads a Statuspage v2 <c>summary.json</c>. Only the components that matter for the provider count, so an
/// outage of an unrelated product (an image generator, a web login) does not raise a badge. If none of the
/// watched components is found (the page renamed them) the page's overall indicator is used instead.
/// Returns null for anything that is not a recognizable summary: a status check must never become an error.
/// </summary>
public static class StatusPageParser
{
    private const int MaxDetailLength = 140;

    public static StatusReport? Parse(string json, IReadOnlyList<string> watchedComponents)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var anyWatched = false;
            var affected = new List<(string Name, string Status, OutageLevel Level)>();
            if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
            {
                foreach (var component in components.EnumerateArray())
                {
                    if (component.ValueKind != JsonValueKind.Object) continue;
                    var name = JsonHelpers.ReadString(component, "name");
                    if (name is null || !watchedComponents.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                    anyWatched = true;
                    var status = JsonHelpers.ReadString(component, "status") ?? string.Empty;
                    var level = ComponentLevel(status);
                    if (level != OutageLevel.None) affected.Add((name, status, level));
                }
            }

            if (anyWatched)
            {
                if (affected.Count == 0) return new StatusReport(OutageLevel.None, null);

                var detail = string.Join(", ", affected.Select(a => $"{a.Name}: {Describe(a.Status)}"));
                return new StatusReport(affected.Max(a => a.Level), Shorten(detail));
            }

            if (!root.TryGetProperty("status", out var overall) || overall.ValueKind != JsonValueKind.Object) return null;
            var indicator = JsonHelpers.ReadString(overall, "indicator");
            if (indicator is null) return null;

            var overallLevel = indicator switch
            {
                "minor" => OutageLevel.Degraded,
                "major" or "critical" => OutageLevel.Outage,
                _ => OutageLevel.None,
            };
            var description = overallLevel == OutageLevel.None ? null : JsonHelpers.ReadString(overall, "description");
            return new StatusReport(overallLevel, description is null ? null : Shorten(description));
        }
    }

    // Maintenance is announced work, not an outage.
    private static OutageLevel ComponentLevel(string status) => status switch
    {
        "degraded_performance" or "partial_outage" => OutageLevel.Degraded,
        "major_outage" => OutageLevel.Outage,
        _ => OutageLevel.None,
    };

    private static string Describe(string status) => status switch
    {
        "degraded_performance" => L.T("degraded performance", "düşük performans"),
        "partial_outage" => L.T("partial outage", "kısmi kesinti"),
        "major_outage" => L.T("major outage", "büyük kesinti"),
        _ => status,
    };

    private static string Shorten(string text) =>
        text.Length <= MaxDetailLength ? text : text[..(MaxDetailLength - 1)] + "…";
}
