using System.Text.Json;

namespace Ration.Core.Cost;

/// <summary>
/// Extracts the SHAPE of JSONL files: only key paths and value types.
///
/// Session logs contain conversation content. Reading or sharing those files as they
/// are, just to learn the schema, is a privacy violation. This probe only produces lines like
/// "payload.info.input_tokens : Number" — no value, no text and no id ever comes out.
/// </summary>
public static class JsonlSchemaProbe
{
    public static IReadOnlyList<string> DescribeKeyPaths(
        string directory,
        string? mustContainKey = null,
        int maxFiles = 5,
        int maxLinesPerFile = 2000,
        CancellationToken ct = default)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(directory)) return new[] { $"(no directory: {directory})" };

        IEnumerable<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(maxFiles);
        }
        catch (UnauthorizedAccessException)
        {
            return new[] { "(directory could not be read)" };
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                var lines = 0;

                while (reader.ReadLine() is { } line && lines++ < maxLinesPerFile)
                {
                    if (line.Length == 0) continue;

                    if (mustContainKey is not null &&
                        !line.Contains(mustContainKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        Walk(document.RootElement, prefix: string.Empty, paths, depth: 0);
                    }
                    catch (JsonException) { /* partial line: skip */ }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return paths.ToList();
    }

    private static void Walk(JsonElement element, string prefix, SortedSet<string> into, int depth)
    {
        if (depth > 8) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    Walk(property.Value, path, into, depth + 1);
                }
                break;

            case JsonValueKind.Array:
                // The shape of only the first array element is enough.
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{prefix}[]", into, depth + 1);
                    break;
                }
                break;

            default:
                // Only the path and TYPE are written — never the value.
                into.Add($"{prefix} : {element.ValueKind}");
                break;
        }
    }
}
