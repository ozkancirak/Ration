using System.Text.Json;
using Ration.Core.Providers;

namespace Ration.Core.Cost;

/// <summary>
/// ~/.codex/sessions/YYYY/MM/DD/*.jsonl içindeki token_count olaylarını toplar.
///
/// UYARI: Claude'un aksine bu şema henüz gerçek veriyle doğrulanmadı. Ayrıştırma
/// toleranslı yazıldı ve tanımadığı satırı sessizce atlar; hiçbir koşulda uydurma
/// sayı üretmez. Şemayı görmek için: ration cost --schema
///
/// GİZLİLİK: yalnızca sayılar ve model adı okunur, konuşma içeriği hiç açılmaz.
///
/// Çift sayım koruması: bir olay "bu adımda kullanılan" (last_token_usage) ya da
/// "oturum toplamı" (total_token_usage) bildirebilir. total_token_usage varsa
/// kümülatif ilerleme delta olarak alınır; yalnızca last_token_usage varsa aynı
/// bildirimin tekrarı tekilleştirilir.
/// </summary>
public static class CodexCostScanner
{
    public static CostScanResult Scan(
        DateTimeOffset since,
        string? sessionsDirectory = null,
        CancellationToken ct = default)
    {
        var directory = sessionsDirectory ?? KnownPaths.CodexSessionsDir;
        var tally = new TokenTally();
        var now = DateTimeOffset.UtcNow;

        if (!Directory.Exists(directory))
        {
            return new CostScanResult(tally, since, now, 0, $"Dizin bulunamadı: {directory}");
        }

        var filesScanned = 0;
        var periodKnown = true;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            return new CostScanResult(tally, since, now, 0, "Dizin okunamadı (yetki).");
        }

        // Bazı oturumlarda (ör. codex-auto-review) olaylar model adı taşımaz; Codex modeli kendi
        // durum veritabanında oturum dosyasına göre tutar. Yoksa "(bilinmeyen model)" çıkıyordu.
        var threadModels = ReadThreadModels(Path.GetDirectoryName(Path.GetFullPath(directory)));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                filesScanned++;
                ScanFile(reader, since, tally, ref periodKnown, threadModels.GetValueOrDefault(Path.GetFullPath(file)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var note = tally.IsEmpty
            ? "Token verisi bulunamadı. Şema doğrulanmamış — 'ration cost --schema' ile anahtarları görebilirsiniz."
            : "Şema henüz gerçek veriyle doğrulanmadı; sayıları bir kez teyit edin.";

        return new CostScanResult(tally, since, now, filesScanned, note)
        {
            PeriodKnown = periodKnown,
        };
    }

    private sealed record TokenEvent(
        string? Model,
        long[] Usage,
        DateTimeOffset? At,
        bool Cumulative,
        string? Identity);

    private static void ScanFile(
        StreamReader reader,
        DateTimeOffset since,
        TokenTally tally,
        ref bool periodKnown,
        string? fallbackModel = null)
    {
        string? currentModel = fallbackModel;
        var events = new List<TokenEvent>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object) continue;

                var model = FindModel(root);
                if (model is not null) currentModel = model;

                var info = FindTokenCountInfo(root);
                if (info is null) continue;

                // total_token_usage kümülatiftir. Aynı olayda hem total hem last
                // varsa yalnızca total kullanılır; iki alanı toplamak yanlıştır.
                var total = ReadUsage(info.Value, "total_token_usage");
                if (total is not null)
                {
                    events.Add(new TokenEvent(
                        currentModel,
                        total,
                        ReadEventTimestamp(root),
                        Cumulative: true,
                        ReadEventIdentity(root)));
                    continue;
                }

                var incremental = ReadUsage(info.Value, "last_token_usage") ?? ReadUsageDirect(info.Value);
                if (incremental is not null)
                {
                    events.Add(new TokenEvent(
                        currentModel,
                        incremental,
                        ReadEventTimestamp(root),
                        Cumulative: false,
                        ReadEventIdentity(root)));
                }
            }
            catch (JsonException) { }
        }

        if (events.Any(item => item.Cumulative))
        {
            AddCumulative(events.Where(item => item.Cumulative), since, tally, ref periodKnown);
        }
        else
        {
            AddIncremental(events, since, tally, ref periodKnown);
        }
    }

    private static void AddCumulative(
        IEnumerable<TokenEvent> events,
        DateTimeOffset since,
        TokenTally tally,
        ref bool periodKnown)
    {
        long[]? previous = null;

        // Normal Codex logları kronolojiktir; sıralama, dosyanın kısmi yeniden
        // yazıldığı durumda sayaç delta'sının ters dönmesini de engeller.
        foreach (var item in events.OrderBy(item => item.At ?? DateTimeOffset.MaxValue))
        {
            if (!IncludeInPeriod(item.At, since, ref periodKnown))
            {
                previous = (long[])item.Usage.Clone();
                continue;
            }

            var delta = previous is null || HasDecreased(item.Usage, previous)
                ? item.Usage
                : Subtract(item.Usage, previous);

            previous = (long[])item.Usage.Clone();
            if (Sum(delta) == 0) continue;

            tally.Add(item.Model, delta[0], delta[1], delta[2], delta[3], delta[4], item.At);
        }
    }

    private static void AddIncremental(
        IEnumerable<TokenEvent> events,
        DateTimeOffset since,
        TokenTally tally,
        ref bool periodKnown)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in events)
        {
            if (!IncludeInPeriod(item.At, since, ref periodKnown)) continue;

            // Aynı JSON satırı/olay iki kez yazılmışsa kullanım artırılmaz. Olay
            // kimliği varsa onu, yoksa sağlayıcı kullanım vektörünü kullan.
            var fingerprint = item.Identity is { Length: > 0 }
                ? $"id\0{item.Identity}"
                : $"usage\0{item.Model}\0{string.Join(',', item.Usage)}";
            if (!seen.Add(fingerprint)) continue;

            tally.Add(item.Model, item.Usage[0], item.Usage[1], item.Usage[2], item.Usage[3], item.Usage[4], item.At);
        }
    }

    private static bool IncludeInPeriod(
        DateTimeOffset? at,
        DateTimeOffset since,
        ref bool periodKnown)
    {
        if (at is null)
        {
            periodKnown = false;
            return true;
        }

        return at >= since;
    }

    private static bool HasDecreased(long[] current, long[] previous) =>
        current[0] < previous[0] ||
        current[1] < previous[1] ||
        current[2] < previous[2] ||
        current[3] < previous[3] ||
        current[4] < previous[4];

    private static long[] Subtract(long[] current, long[] previous) =>
        new[]
        {
            Math.Max(0, current[0] - previous[0]),
            Math.Max(0, current[1] - previous[1]),
            Math.Max(0, current[2] - previous[2]),
            Math.Max(0, current[3] - previous[3]),
            Math.Max(0, current[4] - previous[4]),
        };

    // Reasoning alt küme olduğu için büyüklük karşılaştırmasına dahil edilmez.
    private static long Sum(long[] usage) => usage[0] + usage[1] + usage[2] + usage[3];

    /// <summary>token_count olayının bilgi nesnesini bulur; yoksa null.</summary>
    private static JsonElement? FindTokenCountInfo(JsonElement root)
    {
        // Biçim 1: { type: "event_msg", payload: { type: "token_count", info: {...} } }
        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object &&
            IsTokenCount(payload))
        {
            return payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                ? info
                : payload;
        }

        // Biçim 2: { type: "token_count", info: {...} }
        if (IsTokenCount(root))
        {
            return root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                ? info
                : root;
        }

        return null;
    }

    private static bool IsTokenCount(JsonElement element) =>
        element.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), "token_count", StringComparison.OrdinalIgnoreCase);

    private static long[]? ReadUsage(JsonElement parent, string childName) =>
        parent.TryGetProperty(childName, out var child) && child.ValueKind == JsonValueKind.Object
            ? ReadUsageDirect(child)
            : null;

    private static long[]? ReadUsageDirect(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var rawInput = ReadLong(element, "input_tokens");
        var output = ReadLong(element, "output_tokens");
        var cacheRead = Math.Max(
            ReadLong(element, "cached_input_tokens"),
            ReadLong(element, "cache_read_input_tokens"));

        // Codex input_tokens cache dahil toplam girdidir. Cache'i ikinci kez
        // saymamak için sağlayıcı sınırında normal girdiye ayır.
        var input = Math.Max(0, rawInput - cacheRead);

        // Codex "cache_write_input_tokens" der. İkinci ad yalnızca toleranslı
        // okuma içindir; iki isim aynı değeri taşıyorsa iki kez toplama.
        var cacheWrite = Math.Max(
            ReadLong(element, "cache_write_input_tokens"),
            ReadLong(element, "cache_creation_input_tokens"));

        // reasoning_output_tokens, output_tokens'ın ALT KÜMESİdır; ayrı tutulur,
        // toplama eklenmez (eklenirse çıktı iki kez sayılır).
        var reasoning = ReadLong(element, "reasoning_output_tokens");

        // Hiçbir token alanı yoksa bu bir kullanım nesnesi değildir.
        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0) return null;

        return new[] { input, output, cacheRead, cacheWrite, reasoning };
    }

    private static DateTimeOffset? ReadEventTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var timestamp) &&
            ReadTimestamp(timestamp) is { } at)
        {
            return at;
        }

        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("timestamp", out var payloadTimestamp))
        {
            return ReadTimestamp(payloadTimestamp);
        }

        return null;
    }

    private static string? ReadEventIdentity(JsonElement root)
    {
        foreach (var name in new[] { "id", "event_id", "message_id", "response_id" })
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "id", "event_id", "message_id", "response_id" })
            {
                if (payload.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var epoch))
        {
            return epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }

    /// <summary>
    /// Codex'in durum veritabanından (~/.codex/state_N.sqlite, threads tablosu) oturum dosyası
    /// → model eşlemesi. Salt okunur; yalnızca rollout_path ve model sütunları okunur, konuşma
    /// içeriğine dokunulmaz. Veritabanı yoksa ya da şema değişmişse boş döner.
    /// </summary>
    private static Dictionary<string, string> ReadThreadModels(string? codexHome)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (codexHome is null || !Directory.Exists(codexHome)) return result;

        var database = Directory.EnumerateFiles(codexHome, "state_*.sqlite")
            .OrderByDescending(path => int.TryParse(
                Path.GetFileNameWithoutExtension(path)["state_".Length..], out var version) ? version : -1)
            .FirstOrDefault();
        if (database is null) return result;

        try
        {
            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rollout_path, model FROM threads WHERE model IS NOT NULL AND rollout_path IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[Path.GetFullPath(reader.GetString(0))] = reader.GetString(1);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Codex şemasını değiştirirse yalnızca bu yedek kaybolur; tarama sürer.
        }

        return result;
    }

    private static string? FindModel(JsonElement root)
    {
        if (root.TryGetProperty("turn_context", out var turnContext) &&
            turnContext.ValueKind == JsonValueKind.Object)
        {
            var fromContext = ReadString(turnContext, "model");
            if (fromContext is not null) return fromContext;
        }

        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object)
        {
            var fromPayload = ReadString(payload, "model");
            if (fromPayload is not null) return fromPayload;
        }

        return ReadString(root, "model");
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0L;
}
