using System.Text.Json;

namespace Kalan.Core.Discovery;

/// <summary>Keşfedilen tek dosya: yol + boyut + (JSON ise) şema. DEĞER YOK.</summary>
public sealed record DiscoveredFile(
    string Path,
    long Size,
    IReadOnlyList<string>? Schema);

/// <summary>Bir sağlayıcının dizin keşif raporu.</summary>
public sealed record DiscoveryReport(
    string Provider,
    IReadOnlyList<string> Roots,
    IReadOnlyList<DiscoveredFile> Files,
    IReadOnlyList<string> Notes);

/// <summary>
/// Sağlayıcı keşfi: dosya YOLLARI + anahtar yolları + değer TÜRLERİ (+ string uzunluğu).
/// <see cref="Cost.JsonlSchemaProbe"/> felsefesi: hiçbir DEĞER çıktığa girmez —
/// token, e-posta, id, hesap adı bu raporda görünemez (daha önce bir kez yaşandı).
/// Dosyalar salt okunur açılır (FileShare.ReadWrite), kilit tutulmaz.
/// Bu modül AĞA ÇIKMAZ ve provider kodu üretmez; yalnızca keşif.
/// </summary>
public static class ProviderDiscovery
{
    private const int MaxFiles = 50;
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxLinesPerJsonl = 200;
    private const int MaxFilesPerJsonl = 5;
    private const int MaxKeysPerFile = 300;
    private const int MaxDepth = 6;

    /// <summary>Bilinen kökler. Tahmin yok: yalnızca istenen dizinler taranır.</summary>
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
                notes.Add($"(dizin yok: {root})");
                continue;
            }

            List<string> entries;
            try
            {
                // Derinlik öncelikli: köktekiler (kimlik/ayar adayları) önce,
                // derin yedek ağaçları sonra — cap derinlikte kaybolmasın.
                entries = Directory
                    .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
                    .ThenBy(f => f, StringComparer.Ordinal)
                    .Take(MaxFiles)
                    .ToList();
            }
            catch (UnauthorizedAccessException)
            {
                notes.Add($"(dizin okunamadı: {root})");
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
        catch (JsonException) { return new[] { "(JSON ayrıştırılamadı)" }; }
        catch (IOException) { return new[] { "(dosya okunamadı)" }; }
        catch (UnauthorizedAccessException) { return new[] { "(dosya okunamadı)" }; }
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
                catch (JsonException) { /* yarım satır: atla */ }
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
                // Dizinin yalnızca ilk elemanının şekli yeterli.
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{prefix}[]", into, depth + 1);
                    break;
                }
                break;

            case JsonValueKind.String:
                // Uzunluk bilgisi yeter; içerik ASLA yazılmaz.
                into.Add($"{prefix} : String(len={element.GetString()?.Length ?? 0})");
                break;

            default:
                into.Add($"{prefix} : {element.ValueKind}");
                break;
        }
    }
}
