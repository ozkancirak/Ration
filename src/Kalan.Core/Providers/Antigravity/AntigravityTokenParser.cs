using System.Globalization;
using System.Text.Json;
using Kalan.Core.Cost;

namespace Kalan.Core.Providers.Antigravity;

public static class AntigravityTokenParser
{
    public static bool TryReadCascadeIds(
        string json,
        out IReadOnlyList<string> cascadeIds)
    {
        cascadeIds = Array.Empty<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = Unwrap(document.RootElement);
            if (!root.TryGetProperty("trajectorySummaries", out var summaries) ||
                summaries.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            cascadeIds = summaries.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.Object)
                .Select(property => property.Name)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static int AddGeneratorMetadataToTally(
        string json,
        TokenTally tally,
        ISet<string> seenIds,
        out int skipped)
    {
        skipped = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = Unwrap(document.RootElement);
            if (!root.TryGetProperty("generatorMetadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var accepted = 0;
            foreach (var item in metadata.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("chatModel", out var chatModel) ||
                    chatModel.ValueKind != JsonValueKind.Object ||
                    !TryReadString(chatModel, "responseModel", out var model) ||
                    !chatModel.TryGetProperty("usage", out var usage) ||
                    usage.ValueKind != JsonValueKind.Object ||
                    !TryReadLong(usage, "inputTokens", out var input) ||
                    !TryReadLong(usage, "outputTokens", out var output) ||
                    !TryReadLong(usage, "thinkingOutputTokens", out var thinking) ||
                    !TryReadLong(usage, "cacheReadTokens", out var cacheRead) ||
                    !TryReadLong(usage, "cacheWriteTokens", out var cacheWrite))
                {
                    skipped++;
                    continue;
                }

                var responseId = TryReadString(usage, "responseId", out var id)
                    ? id
                    : TryReadString(usage, "messageId", out var fallbackId)
                        ? fallbackId
                        : null;
                if (string.IsNullOrWhiteSpace(responseId) || !seenIds.Add(responseId))
                {
                    skipped++;
                    continue;
                }

                if (input < 0 || output < 0 || thinking < 0 || cacheRead < 0 || cacheWrite < 0)
                {
                    skipped++;
                    continue;
                }

                tally.Add(
                    model,
                    input,
                    output,
                    cacheRead,
                    cacheWrite,
                    Math.Min(thinking, output));
                accepted++;
            }

            return accepted;
        }
        catch (JsonException)
        {
            skipped++;
            return 0;
        }
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object)
        {
            return response;
        }

        return root;
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadLong(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property)) return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(
                   property.GetString(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value);
    }
}
