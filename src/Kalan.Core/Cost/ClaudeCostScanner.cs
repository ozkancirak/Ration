using System.Text.Json;
using Kalan.Core.Providers;

namespace Kalan.Core.Cost;

/// <summary>
/// ~/.claude/projects/**/*.jsonl içindeki asistan yanıtlarının token sayımlarını toplar.
///
/// GİZLİLİK: bu tarayıcı yalnızca sayıları ve kimlikleri okur. Konuşma içeriği
/// (message.content) hiç açılmaz, hiçbir yere yazılmaz, hiçbir çıktıya girmez.
///
/// Dosyalar Claude Code tarafından aktif olarak yazılıyor olabilir; bu yüzden
/// FileShare.ReadWrite ile ve satır satır akıtılarak okunur (tümü belleğe alınmaz).
/// </summary>
public static class ClaudeCostScanner
{
    public static CostScanResult Scan(
        DateTimeOffset since,
        string? projectsDirectory = null,
        CancellationToken ct = default)
    {
        var directory = projectsDirectory ?? KnownPaths.ClaudeProjectsDir;
        var tally = new TokenTally();
        var now = DateTimeOffset.UtcNow;

        if (!Directory.Exists(directory))
        {
            return new CostScanResult(tally, since, now, 0, $"Dizin bulunamadı: {directory}");
        }

        // Aynı yanıt birden fazla satırda görünebilir (yeniden yazım, devam kaydı).
        var seen = new HashSet<string>(StringComparer.Ordinal);
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
                // Hızlı eleme: dosyaya son yazma tarihimizden önceyse hiç açma.
                if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime) continue;

                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                filesScanned++;

                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0) continue;

                    ReadLine(line, since, seen, tally);
                }
            }
            catch (IOException) { /* dosya kilitli ya da silinmiş: atla */ }
            catch (UnauthorizedAccessException) { /* atla */ }
        }

        return new CostScanResult(tally, since, now, filesScanned);
    }

    private static void ReadLine(string line, DateTimeOffset since, HashSet<string> seen, TokenTally tally)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return;

            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!message.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // Zaman filtresi: satırda timestamp varsa kullan, yoksa dahil et.
            if (root.TryGetProperty("timestamp", out var timestampElement))
            {
                var timestamp = ReadTimestamp(timestampElement);
                if (timestamp is not null && timestamp < since) return;
            }

            // Tekilleştirme: message.id + requestId
            var messageId = ReadString(message, "id");
            var requestId = ReadString(root, "requestId") ?? ReadString(root, "request_id");
            var key = $"{messageId}|{requestId}";

            if (messageId is not null && !seen.Add(key)) return;

            // output_tokens_details.thinking_tokens, output_tokens'ın ALT KÜMESİdir
            // (Codex'teki reasoning_output_tokens ile aynı desen): ayrı tutulur,
            // toplama eklenmez.
            var thinking = 0L;
            if (usage.TryGetProperty("output_tokens_details", out var details) &&
                details.ValueKind == JsonValueKind.Object)
            {
                thinking = ReadLong(details, "thinking_tokens") + ReadLong(details, "reasoning_tokens");
            }

            tally.Add(
                ReadString(message, "model"),
                ReadLong(usage, "input_tokens"),
                ReadLong(usage, "output_tokens"),
                ReadLong(usage, "cache_read_input_tokens"),
                ReadLong(usage, "cache_creation_input_tokens"),
                thinking);
        }
        catch (JsonException)
        {
            // Yarım yazılmış son satır normaldir; sessizce atla.
        }
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
