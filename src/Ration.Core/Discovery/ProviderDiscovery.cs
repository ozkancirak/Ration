using System.Text.Json;

namespace Ration.Core.Discovery;

/// <summary>The single discovered file: path + size + (if JSON) schema. NO VALUES.</summary>
public sealed record DiscoveredFile(
    string Path,
    long Size,
    IReadOnlyList<string>? Schema);

/// <summary>A provider's directory discovery report.</summary>
public sealed record DiscoveryReport(
    string Provider,
    IReadOnlyList<string> Roots,
    IReadOnlyList<DiscoveredFile> Files,
    IReadOnlyList<string> Notes);

/// <summary>
/// Provider discovery: file PATHS + key paths + value TYPES (+ string length).
/// The philosophy of <see cref="Cost.JsonlSchemaProbe"/>: no VALUE enters the output —
/// tokens, emails, ids and account names cannot appear in this report (this has already happened once).
/// Files are opened read-only (FileShare.ReadWrite) and no lock is held.
/// This module NEVER touches the network and does not generate provider code; it only discovers.
/// </summary>
public static class ProviderDiscovery
{
    private const int MaxFiles = 50;
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxLinesPerJsonl = 200;
    private const int MaxFilesPerJsonl = 5;
    private const int MaxKeysPerFile = 300;
    private const int MaxDepth = 6;

    /// <summary>Known roots. No guessing: only the requested directories are scanned.</summary>
    public static IReadOnlyList<string>? RootsFor(string provider)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return provider.ToLowerInvariant() switch
        {
            "gemini" => new[] { Path.Combine(home, ".gemini") },
            "copilot" => new[]
            {
                Path.Combine(home, ".copilot"),
                Path.Combine(home, ".config", "github-copilot"),
            },
            _ => null,
        };
    }

    public static DiscoveryReport Discover(string provider, string? rootOverride = null)
    {
        var roots = rootOverride is not null
            ? new[] { rootOverride }
            : RootsFor(provider) ?? Array.Empty<string>();

        var files = new List<DiscoveredFile>();
        var notes = new List<string>();
        var jsonlSeen = 0;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                notes.Add($"(no directory: {root})");
                continue;
            }

            List<string> entries;
            try
            {
                // Ordered by depth: files at the root (credential/settings candidates) come first and
                // deep backup trees last, so the cap is not used up on the deep ones.
                entries = Directory
                    .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
                    .ThenBy(f => f, StringComparer.Ordinal)
                    .Take(MaxFiles)
                    .ToList();
            }
            catch (UnauthorizedAccessException)
            {
                notes.Add($"(directory could not be read: {root})");
                continue;
            }

            foreach (var entry in entries)
            {
                long size;
                try
                {
                    size = new FileInfo(entry).Length;
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                IReadOnlyList<string>? schema = null;
                var ext = Path.GetExtension(entry);
                if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) && size <= MaxJsonBytes)
                {
                    schema = DescribeJsonFile(entry);
                }
                else if (ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) && jsonlSeen < MaxFilesPerJsonl)
                {
                    jsonlSeen++;
                    schema = DescribeJsonlFile(entry);
                }

                files.Add(new DiscoveredFile(entry, size, schema));
            }
        }

        return new DiscoveryReport(provider, roots, files, notes);
    }

    private static IReadOnlyList<string> DescribeJsonFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxJsonBytes) return Array.Empty<string>();
            using var document = JsonDocument.Parse(stream);
            return WalkToList(document.RootElement);
        }
        catch (JsonException) { return new[] { "(JSON could not be parsed)" }; }
        catch (IOException) { return new[] { "(file could not be read)" }; }
        catch (UnauthorizedAccessException) { return new[] { "(file could not be read)" }; }
    }

    private static IReadOnlyList<string> DescribeJsonlFile(string path)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = 0;
            while (reader.ReadLine() is { } line && lines++ < MaxLinesPerJsonl)
            {
                if (line.Length == 0) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    Walk(document.RootElement, string.Empty, paths, 0);
                }
                catch (JsonException) { /* partial line: skip */ }
                if (paths.Count >= MaxKeysPerFile) break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return paths.ToList();
    }

    private static IReadOnlyList<string> WalkToList(JsonElement root)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        Walk(root, string.Empty, paths, 0);
        return paths.ToList();
    }

    private static void Walk(JsonElement element, string prefix, SortedSet<string> into, int depth)
    {
        if (depth > MaxDepth || into.Count >= MaxKeysPerFile) return;

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

            case JsonValueKind.String:
                // The length is enough; the content is NEVER written.
                into.Add($"{prefix} : String(len={element.GetString()?.Length ?? 0})");
                break;

            default:
                into.Add($"{prefix} : {element.ValueKind}");
                break;
        }
    }
}
