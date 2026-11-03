using System.Text.Json;

namespace Ration.Core.Providers;

internal static class JsonHelpers
{
    /// <summary>
    /// Reads a timestamp as an ISO-8601 string or a Unix epoch (seconds or milliseconds).
    /// Returns null for an unrecognized format — it does not make up a date.
    /// </summary>
    public static DateTimeOffset? ReadTimestamp(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var epoch))
        {
            // The 100-billion threshold safely tells seconds from milliseconds:
            // in seconds 100e9 corresponds to the year 5138, in milliseconds to 1973.
            return epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }

    /// <summary>Returns the value if the field is a string, null if it is missing or of another type.</summary>
    public static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Returns the first numeric field among the given names; null if there is none.</summary>
    public static double? ReadFirstNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDouble();
            }
        }

        return null;
    }
}
