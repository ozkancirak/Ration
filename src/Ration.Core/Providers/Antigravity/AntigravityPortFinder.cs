using System.Text.RegularExpressions;

namespace Ration.Core.Providers.Antigravity;

/// <summary>
/// Antigravity cli.log içindeki portu yalnızca salt-okunur gözlemle bulur.
/// Süreç command line'ı Windows katmanında WMI ile okunur; burada yalnızca
/// log parser'ı ve port doğrulaması yaşar.
/// </summary>
public static class AntigravityPortFinder
{
    private static readonly Regex ListeningLine = new(
        @"listening\s+on\s+random\s+port\s+at\s+(?<port>\d{1,5})\s+for\s+HTTP\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int? FindNewestLogPort(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return null;

            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return FindPortInLogText(reader.ReadToEnd());
        }
        catch (IOException)
        {
            // The CLI may rotate the file while it is being inspected.
        }
        catch (UnauthorizedAccessException)
        {
            // A missing read permission is equivalent to no usable log.
        }

        return null;
    }

    public static int? FindPortInLogText(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var matches = ListeningLine.Matches(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            if (TryReadPort(matches[i].Groups["port"].Value, out var port))
            {
                return port;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> ReadLastListeningLines(string logPath, int count = 5)
    {
        if (count <= 0) return Array.Empty<string>();

        try
        {
            if (!File.Exists(logPath)) return Array.Empty<string>();

            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var matches = new Queue<string>(count);

            while (reader.ReadLine() is { } line)
            {
                if (!ListeningLine.IsMatch(line)) continue;
                if (matches.Count == count) matches.Dequeue();
                matches.Enqueue(line);
            }

            return matches.ToArray();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return Array.Empty<string>();
    }

    private static bool TryReadPort(string value, out int port)
    {
        return int.TryParse(value, out port) && port is >= 1 and <= 65535;
    }
}
