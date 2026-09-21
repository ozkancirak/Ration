using System.Text.Json;
using Kalan.Core.Model;

namespace Kalan.Core.Providers.Antigravity;

public static class AntigravityUsageParser
{
    public sealed record ParseResult(
        IReadOnlyList<UsageWindow> Windows,
        string? PlanName);

    public static IReadOnlyList<UsageWindow> ParseWindows(string json) =>
        Parse(json).Windows;

    public static ParseResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var groups = FindArray(document.RootElement, "groups", "quotaGroups")
            ?? FindNestedGroups(document.RootElement);
        if (groups is null) return new ParseResult(Array.Empty<UsageWindow>(), null);

        var windows = new List<UsageWindow>();
        foreach (var group in groups.Value.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;

            var groupName = ReadString(group, "displayName");
            var groupDescription = ReadString(group, "description");
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
                    Label: WindowLabel(kind),
                    WindowLength: length,
                    GroupName: groupName,
                    GroupDescription: groupDescription));
            }
        }

        // RetrieveUserQuotaSummary plan döndürmez. Plan bilgisi ayrı GetUserStatus
        // çağrısından gelir; kota gövdesinden tahminde bulunmak rozetleri kirletir.
        return new ParseResult(windows, null);
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

    private static string WindowLabel(WindowKind kind) => kind switch
    {
        WindowKind.Session => "5 saatlik",
        WindowKind.Weekly => "Haftalık",
        _ => kind.ToString(),
    };

    public static string? ParsePlanName(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var element in EnumerateStatusContainers(root))
        {
            var status = element;
            if (element.TryGetProperty("userStatus", out var nestedStatus) &&
                nestedStatus.ValueKind == JsonValueKind.Object)
            {
                status = nestedStatus;
            }

            var paths = new[]
            {
                new[] { "userTier", "name" },
                new[] { "userTier", "description" },
                new[] { "planStatus", "planInfo", "planDisplayName" },
                new[] { "planStatus", "planInfo", "planName" },
            };
            foreach (var path in paths)
            {
                var value = ReadPath(status, path);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateStatusContainers(JsonElement root)
    {
        yield return root;

        foreach (var wrapperName in new[] { "response", "userStatus" })
        {
            if (root.TryGetProperty(wrapperName, out var wrapper) &&
                wrapper.ValueKind == JsonValueKind.Object)
            {
                yield return wrapper;
            }
        }
    }

    private static string? ReadPath(JsonElement element, IReadOnlyList<string> path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
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
