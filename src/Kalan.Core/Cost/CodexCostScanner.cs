using System.Text.Json;
using Kalan.Core.Providers;

namespace Kalan.Core.Cost;

/// <summary>
/// ~/.codex/sessions/YYYY/MM/DD/*.jsonl içindeki token_count olaylarını toplar.
///
/// UYARI: Claude'un aksine bu şema henüz gerçek veriyle doğrulanmadı. Ayrıştırma
/// toleranslı yazıldı ve tanımadığı satırı sessizce atlar; hiçbir koşulda uydurma
/// sayı üretmez. Şemayı görmek için: kalan cost --schema
///
/// GİZLİLİK: yalnızca sayılar ve model adı okunur, konuşma içeriği hiç açılmaz.
///
/// Çift sayım koruması: bir olay "bu adımda kullanılan" (last_token_usage) ya da
/// "oturum toplamı" (total_token_usage) bildirebilir. İlki toplanır; yalnızca
/// ikincisi varsa dosya başına EN BÜYÜĞÜ bir kez eklenir.
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

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            return new CostScanResult(tally, since, now, 0, "Dizin okunamadı (yetki).");
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime) continue;

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                filesScanned++;
                ScanFile(reader, tally);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var note = tally.IsEmpty
            ? "Token verisi bulunamadı. Şema doğrulanmamış — 'kalan cost --schema' ile anahtarları görebilirsiniz."
            : "Şema henüz gerçek veriyle doğrulanmadı; sayıları bir kez teyit edin.";

        return new CostScanResult(tally, since, now, filesScanned, note);
    }

    private static void ScanFile(StreamReader reader, TokenTally tally)
    {
        string? currentModel = null;

        // Dosya boyunca yalnızca "toplam" bildiren olaylar varsa, en büyüğünü
        // bir kez ekleriz. Adım bazlı (last) veri geldiyse toplam hiç kullanılmaz.
        var sawIncremental = false;
        long[]? bestTotal = null;
        string? bestTotalModel = null;

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

                var incremental = ReadUsage(info.Value, "last_token_usage");

                if (incremental is not null)
                {
                    sawIncremental = true;
                    tally.Add(currentModel, incremental[0], incremental[1], incremental[2], incremental[3], incremental[4]);
                    continue;
                }

                var total = ReadUsage(info.Value, "total_token_usage") ?? ReadUsageDirect(info.Value);

                if (total is not null && (bestTotal is null || Sum(total) > Sum(bestTotal)))
                {
                    bestTotal = total;
                    bestTotalModel = currentModel;
                }
            }
            catch (JsonException) { }
        }

        if (!sawIncremental && bestTotal is not null)
        {
            tally.Add(bestTotalModel, bestTotal[0], bestTotal[1], bestTotal[2], bestTotal[3], bestTotal[4]);
        }
    }

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

        var input = ReadLong(element, "input_tokens");
        var output = ReadLong(element, "output_tokens");
        var cacheRead = ReadLong(element, "cached_input_tokens") + ReadLong(element, "cache_read_input_tokens");

        // Codex "cache_write_input_tokens" der, Claude "cache_creation_input_tokens".
        // İkisini de kabul et — ilki eksikti ve önbellek yazma hep 0 görünüyordu.
        var cacheWrite = ReadLong(element, "cache_write_input_tokens")
                       + ReadLong(element, "cache_creation_input_tokens");

        // reasoning_output_tokens, output_tokens'ın ALT KÜMESİdır; ayrı tutulur,
        // toplama eklenmez (eklenirse çıktı iki kez sayılır).
        var reasoning = ReadLong(element, "reasoning_output_tokens");

        // Hiçbir token alanı yoksa bu bir kullanım nesnesi değildir.
        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0) return null;

        return new[] { input, output, cacheRead, cacheWrite, reasoning };
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
