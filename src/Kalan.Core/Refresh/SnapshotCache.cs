using System.Text.Json;
using System.Text.Json.Serialization;
using Kalan.Core.Model;
using Kalan.Core.Providers;

namespace Kalan.Core.Refresh;

/// <summary>
/// Son başarılı snapshot'ları diske yazar.
///
/// Amaç: uygulama açılır açılmaz dolu görünsün ve bir sağlayıcı hata verdiğinde
/// boş kutu yerine "12 dk önceki veri" gösterilebilsin (AGENTS.md §4).
///
/// Yazılabilir tek yer Kalan'ın kendi cache dizinidir; sağlayıcı dosyalarına
/// asla dokunulmaz (AGENTS.md §2.1).
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
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<UsageSnapshot>(stream, Options);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Save(UsageSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            var path = PathFor(snapshot.ProviderId);
            var temporary = path + ".tmp";

            // Önce geçici dosyaya yaz, sonra taşı: yazma sırasında çökersek
            // yarım bir dosya kalıp bir sonraki açılışta okunamaz hale gelmesin.
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, Options);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { /* cache yazılamadıysa sorun değil, veri yine de gösterilir */ }
        catch (UnauthorizedAccessException) { }
    }

    private string PathFor(string providerId)
    {
        // Sağlayıcı kimliği dosya adına giriyor; yol kaçışına izin verme.
        var safe = string.Concat(providerId.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

        return Path.Combine(_directory, $"{safe}.json");
    }
}
