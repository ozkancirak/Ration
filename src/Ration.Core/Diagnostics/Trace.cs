using System.Text.RegularExpressions;
using Ration.Core.Providers;

namespace Ration.Core.Diagnostics;

/// <summary>
/// Ration'ın kalıcı, gizlilik-korumalı günlük yazıcısı.
///
/// Tek süreç içindeki bütün pencereler aynı kilidi paylaşır. Mesajlar yalnızca
/// metadata içermelidir; ham sağlayıcı yanıtları için RawResponse kullanılır.
/// </summary>
public static class Trace
{
    public const long MaxBytes = 256 * 1024;
    private const int RetainedBytesAfterRotation = 128 * 1024;
    private static readonly object Gate = new();
    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretPattern = new(
        @"(?ix)\b(?:bearer|token|api[_-]?key|access[_-]?token|refresh[_-]?token|cookie|authorization)\b\s*[:=]\s*\S+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IdentifierPattern = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string LogPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData)
                ? Path.Combine(AppContext.BaseDirectory, "ration.log")
                : Path.Combine(localAppData, "Ration", "ration.log");
        }
    }

    public static void Info(string category, string message) =>
        Write(category, message);

    public static void Error(string category, string message) =>
        Write(category, $"error={message}");

    /// <summary>
    /// Ham JSON yanıtlarını zorunlu olarak RawResponseRedactor'dan geçirir.
    /// Ayrıştırılamayan içerik de redactor tarafından güvenli metne çevrilir.
    /// </summary>
    public static void RawResponse(string category, string rawJson) =>
        Write(category, RawResponseRedactor.Redact(rawJson));

    public static IReadOnlyList<string> ReadLastLines(int count = 100)
    {
        if (count <= 0) return Array.Empty<string>();

        lock (Gate)
        {
            try
            {
                if (!File.Exists(LogPath)) return Array.Empty<string>();

                using var stream = new FileStream(
                    LogPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                {
                    lines.Add(line);
                    if (lines.Count > count)
                    {
                        lines.RemoveAt(0);
                    }
                }

                return lines;
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }

    private static void Write(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(category)) category = "app";

        var safeCategory = Sanitize(category);
        var safeMessage = Sanitize(message);
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{safeCategory}] {safeMessage}{Environment.NewLine}";

        lock (Gate)
        {
            try
            {
                var path = LogPath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var stream = new FileStream(
                           path,
                           FileMode.Append,
                           FileAccess.Write,
                           FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(line);
                }

                RotateIfNeeded(path);
            }
            catch (IOException)
            {
                // Tanı günlükleri uygulamanın çalışmasını asla durdurmaz.
            }
            catch (UnauthorizedAccessException)
            {
                // Tanı günlükleri uygulamanın çalışmasını asla durdurmaz.
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes) return;

        var bytes = File.ReadAllBytes(path);
        var keep = Math.Min(RetainedBytesAfterRotation, bytes.Length);
        var tail = bytes.AsSpan(bytes.Length - keep, keep).ToArray();
        var tempPath = path + ".tmp";

        try
        {
            File.WriteAllBytes(tempPath, tail);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (IOException) { }
            }
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var safe = EmailPattern.Replace(value, "[email]");
        safe = SecretPattern.Replace(safe, "[gizlendi]");
        safe = IdentifierPattern.Replace(safe, "[id]");
        return safe;
    }
}
