using System.Management;
using System.Text.RegularExpressions;

namespace Kalan.Platform.Windows.Providers;

/// <summary>
/// Antigravity'nin language_server.exe süreçlerini WMI üzerinden salt-okunur
/// sorgular. WMI sonuçları command line değerini dışarı sızdırmaz; yalnızca
/// geçerli --extension_server_port portlarını döndürür.
/// </summary>
public static class AntigravityProcessPortFinder
{
    private static readonly Regex ExtensionServerArgument = new(
        @"(?:^|\s)--extension_server_port(?:=|\s+)(?<port>\d{1,5})(?=\s|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<int> FindPorts()
    {
        var ports = new HashSet<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE Name='language_server.exe'");
            using var processes = searcher.Get();

            foreach (ManagementObject process in processes)
            {
                var commandLine = process["CommandLine"] as string;
                if (string.IsNullOrWhiteSpace(commandLine)) continue;

                foreach (Match match in ExtensionServerArgument.Matches(commandLine))
                {
                    if (int.TryParse(match.Groups["port"].Value, out var port) &&
                        port is >= 1 and <= 65535)
                    {
                        ports.Add(port);
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // WMI unavailable: log sources remain usable.
        }
        catch (UnauthorizedAccessException)
        {
            // A restricted process query is equivalent to no process candidates.
        }

        return ports.ToArray();
    }
}
