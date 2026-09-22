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
        => AddGeneratorMetadataToTally(json, tally, seenIds, out skipped, out _);

    public static int AddGeneratorMetadataToTally(
        string json,
        TokenTally tally,
        ISet<string> seenIds,
        out int skipped,
        out int missingRequired)
    {
        skipped = 0;
        missingRequired = 0;
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
                    chatModel.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                if (!TryReadString(chatModel, "responseModel", out var model) ||
                    !chatModel.TryGetProperty("usage", out var usage) ||
                    usage.ValueKind != JsonValueKind.Object ||
                    !TryReadLong(usage, "inputTokens", out var input))
                {
                    skipped++;
                    missingRequired++;
                    continue;
                }

                // Antigravity does not report cache fields for every model/version.
                // Missing optional fields mean zero; they do not invalidate the record.
                var hasOutput = TryReadLong(usage, "outputTokens", out var outputTokens);
                var hasThinking = TryReadLong(usage, "thinkingOutputTokens", out var thinkingTokens);
                var hasResponse = TryReadLong(usage, "responseOutputTokens", out var responseTokens);
                var output = hasOutput
                    ? outputTokens
                    : SaturatingAdd(
                        hasThinking ? Math.Max(0, thinkingTokens) : 0,
                        hasResponse ? Math.Max(0, responseTokens) : 0);
                var thinking = hasThinking ? Math.Max(0, thinkingTokens) : 0;
                var cacheRead = TryReadLong(usage, "cacheReadTokens", out var cacheReadTokens)
                    ? Math.Max(0, cacheReadTokens)
                    : 0;
                var cacheWrite = TryReadLong(usage, "cacheWriteTokens", out var cacheWriteTokens)
                    ? Math.Max(0, cacheWriteTokens)
                    : 0;

                input = Math.Max(0, input);
                output = Math.Max(0, output);
                thinking = Math.Min(thinking, output);

                var responseId = TryReadString(usage, "responseId", out var id)
                    ? id
                    : TryReadString(usage, "messageId", out var fallbackId)
                        ? fallbackId
                        : null;
                if (!string.IsNullOrWhiteSpace(responseId) && !seenIds.Add(responseId))
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
                    thinking);
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

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
