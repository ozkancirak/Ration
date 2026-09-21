using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Kalan.Core.Providers.Antigravity;

/// <summary>
/// Antigravity'nin yerel language server portunu yalnızca salt-okunur
/// gözlemle bulur. Önce cli.log, sonra çalışan language_server süreçlerinin
/// command line'ı denenir; gRPC portları özellikle eşleştirilmez.
/// </summary>
public static class AntigravityPortFinder
{
    private static readonly Regex ListeningLine = new(
        @"listening\s+on\s+random\s+port\s+at\s+(?<port>\d{1,5})\s+for\s+HTTP\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExtensionServerArgument = new(
        @"(?:^|\s)--extension_server_port(?:=|\s+)(?<port>\d{1,5})(?=\s|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int? FindPort(string logPath)
    {
        try
        {
            if (File.Exists(logPath))
            {
                using var stream = new FileStream(
                    logPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                var fromLog = FindPortInLogText(text);
                if (fromLog is not null) return fromLog;
            }
        }
        catch (IOException)
        {
            // The CLI may rotate the file while it is being inspected.
        }
        catch (UnauthorizedAccessException)
        {
            // A missing read permission is equivalent to no usable log.
        }

        return FindPortFromLanguageServerProcesses();
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

    private static int? FindPortFromLanguageServerProcesses()
    {
        if (!OperatingSystem.IsWindows()) return null;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("language_server");
        }
        catch
        {
            return null;
        }

        foreach (var process in processes)
        {
            try
            {
                var commandLine = WindowsProcessCommandLine.TryRead(process);
                if (commandLine is null) continue;

                var match = ExtensionServerArgument.Match(commandLine);
                if (match.Success && TryReadPort(match.Groups["port"].Value, out var port))
                {
                    return port;
                }
            }
            catch
            {
                // A process can exit between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static bool TryReadPort(string value, out int port)
    {
        return int.TryParse(value, out port) && port is >= 1 and <= 65535;
    }

    private static class WindowsProcessCommandLine
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ProcessCommandLineInformation = 60;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            IntPtr processInformation,
            int processInformationLength,
            out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        public static string? TryRead(Process process)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
            if (handle == IntPtr.Zero) return null;

            try
            {
                _ = NtQueryInformationProcess(
                    handle,
                    ProcessCommandLineInformation,
                    IntPtr.Zero,
                    0,
                    out var requiredLength);
                if (requiredLength <= 0) return null;

                var buffer = Marshal.AllocHGlobal(requiredLength);
                try
                {
                    var status = NtQueryInformationProcess(
                        handle,
                        ProcessCommandLineInformation,
                        buffer,
                        requiredLength,
                        out _);
                    if (status < 0) return null;

                    var commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
                    if (commandLine.Length == 0 || commandLine.Buffer == IntPtr.Zero)
                    {
                        return null;
                    }

                    var start = (nuint)buffer;
                    var end = start + (nuint)requiredLength;
                    var stringStart = (nuint)commandLine.Buffer;
                    var stringEnd = stringStart + commandLine.Length;
                    if (stringStart < start || stringEnd > end)
                    {
                        return null;
                    }

                    return Marshal.PtrToStringUni(commandLine.Buffer, commandLine.Length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
