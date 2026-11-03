using System.Text.Json;
using System.Text.Json.Serialization;
using Ration.Core.Model;
using Ration.Core.Providers;

namespace Ration.Core.Refresh;

/// <summary>
/// Writes the last successful snapshots to disk.
///
/// Purpose: to look populated as soon as the app opens, and so that when a provider
/// fails, "data from 12 min ago" can be shown instead of an empty box.
///
/// The only writable place is Ration's own cache directory; provider files
/// are never touched.
/// </summary>
public sealed class SnapshotCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public SnapshotCache(string? directory = null)
    {
        _directory = directory ?? Path.Combine(KnownPaths.CacheDir, "snapshots");
    }

    public UsageSnapshot? TryLoad(string providerId)
    {
        var path = PathFor(providerId);
        if (!File.Exists(path)) return null;

        try
        {
            UsageSnapshot? snapshot;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                snapshot = JsonSerializer.Deserialize<UsageSnapshot>(stream, Options);
            }

            if (!HasKnownSource(snapshot))
            {
                DeleteInvalidEntry(path);
                return null;
            }

            return snapshot;
        }
        catch (JsonException)
        {
            DeleteInvalidEntry(path);
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Save(UsageSnapshot snapshot)
    {
        // A cache entry without provenance cannot be distinguished from an
        // untrusted result. Never persist it.
        if (!HasKnownSource(snapshot)) return;

        try
        {
            Directory.CreateDirectory(_directory);

            var path = PathFor(snapshot.ProviderId);
            var temporary = path + ".tmp";

            // Write to a temp file first, then move it: if we crash while writing, a half-written file
            // must not remain and become unreadable at the next startup.
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, Options);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { /* if the cache could not be written, no problem, the data is still shown */ }
        catch (UnauthorizedAccessException) { }
    }

    private static bool HasKnownSource(UsageSnapshot? snapshot) =>
        snapshot?.ResolvedVia is { } source &&
        Enum.IsDefined(source);

    private static void DeleteInvalidEntry(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string PathFor(string providerId)
    {
        // The provider id goes into the file name; do not allow path escapes.
        var safe = string.Concat(providerId.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

        return Path.Combine(_directory, $"{safe}.json");
    }
}
