using System.Text.Json;
using Kalan.Core.Model;

namespace Kalan.Core.Providers.Antigravity;

public static class AntigravityUsageParser
{
    public static IReadOnlyList<UsageWindow> ParseWindows(string json)
    {
        using var document = JsonDocument.Parse(json);
        var groups = FindArray(document.RootElement, "groups", "quotaGroups")
            ?? FindNestedGroups(document.RootElement);
        if (groups is null) return Array.Empty<UsageWindow>();

        var windows = new List<UsageWindow>();
        foreach (var group in groups.Value.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;

            var label = ReadString(group, "displayName") ?? "Antigravity";
            var buckets = FindArray(group, "buckets", "quotaBuckets", "windows");
            if (buckets is null) continue;

            foreach (var bucket in buckets.Value.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                if (!TryReadWindow(bucket, out var kind, out var length)) continue;

                var remaining = ReadNumber(bucket, "remainingFraction");
                if (remaining is null || !double.IsFinite(remaining.Value) || remaining is < 0 or > 1)
                {
                    continue;
                }

                var percent = Math.Clamp((1d - remaining.Value) * 100d, 0d, 100d);
                DateTimeOffset? reset = null;
                if (bucket.TryGetProperty("resetTime", out var resetElement))
                {
                    reset = JsonHelpers.ReadTimestamp(resetElement);
                }

                windows.Add(new UsageWindow(
                    Kind: kind,
                    Used: percent,
                    Limit: 100,
                    Percent: percent,
                    ResetsAt: reset,
                    Label: label,
                    WindowLength: length));
            }
        }

        return windows;
    }

    private static JsonElement? FindNestedGroups(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var wrapperName in new[] { "userQuota", "quotaSummary", "userQuotaSummary", "response" })
        {
            if (!root.TryGetProperty(wrapperName, out var wrapper)) continue;
            var groups = FindArray(wrapper, "groups", "quotaGroups");
            if (groups is not null) return groups;
        }

        return null;
    }

    private static JsonElement? FindArray(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryReadWindow(
        JsonElement bucket,
        out WindowKind kind,
        out TimeSpan length)
    {
        kind = default;
        length = default;
        var value = ReadString(bucket, "window") ?? ReadString(bucket, "windowType");
        if (value is null) return false;

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("week", StringComparison.Ordinal))
        {
            kind = WindowKind.Weekly;
            length = TimeSpan.FromDays(7);
            return true;
        }

        if (normalized.Contains("5h", StringComparison.Ordinal) ||
            normalized.Contains("5-hour", StringComparison.Ordinal) ||
            normalized.Contains("session", StringComparison.Ordinal))
        {
            kind = WindowKind.Session;
            length = TimeSpan.FromHours(5);
            return true;
        }

        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }
}
