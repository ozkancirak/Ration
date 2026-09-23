using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Ration.Core.Providers.Antigravity;

namespace Ration.Platform.Windows.Providers;

public sealed record AntigravityProcessInfo(int ProcessId, string Name, string CommandLine);

public sealed record AntigravityListener(int ProcessId, int Port);

/// <summary>
/// Finds Antigravity's actual loopback listeners by PID.
///
/// The listener table is the primary source. Command lines (read natively, see
/// ReadCommandLines) only supply each process's CSRF token and diagnostics.
/// </summary>
public static class AntigravityProcessPortFinder
{
    private const int AddressFamilyIpv4 = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;

    private static readonly Regex CandidateName = new(
        @"(?:language_server|antigravity)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SecretArgument = new(
        @"(?<prefix>(?:--|/)(?:csrf[_-]?token|host[_-]?bridge[_-]?token|access[_-]?token|refresh[_-]?token|api[_-]?key|auth[_-]?token|cookie|password|secret|gpu[_-]?preferences|field[_-]?trial[_-]?handle|pseudonymization[_-]?salt[_-]?handle|trace[_-]?process[_-]?track[_-]?uuid|mojo[_-]?platform[_-]?channel[_-]?handle)(?:=|\s+))(?<value>""[^""]*""|\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SecretQueryValue = new(
        @"(?<prefix>(?:[?&])(?:csrf[_-]?token|access[_-]?token|refresh[_-]?token|token)=)(?<value>[^&\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExtensionCsrfArgument = new(
        @"(?:^|\s)--extension_server_csrf_token(?:=|\s+)(?<value>""[^""]*""|\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CsrfArgument = new(
        @"(?:^|\s)--csrf_token(?:=|\s+)(?<value>""[^""]*""|\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int bufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>
    /// Returns ports owned by candidate processes and listening on 127.0.0.1.
    /// </summary>
    public static IReadOnlyList<int> FindPorts()
    {
        var candidatePids = FindCandidateProcessIds();
        if (candidatePids.Count == 0) return Array.Empty<int>();

        return FindLoopbackListeners()
            .Where(listener => candidatePids.Contains(listener.ProcessId))
            .Select(listener => listener.Port)
            .Distinct()
            .OrderBy(port => port)
            .ToArray();
    }

    public static IReadOnlyList<AntigravityProcessEndpoint> FindEndpoints()
    {
        var candidatePids = FindCandidateProcessIds();
        if (candidatePids.Count == 0) return Array.Empty<AntigravityProcessEndpoint>();

        var commandLines = ReadCommandLines(candidatePids);
        var tokens = commandLines.ToDictionary(
            pair => pair.Key,
            pair => ExtractCsrfToken(pair.Value));

        return FindLoopbackListeners()
            .Where(listener => candidatePids.Contains(listener.ProcessId))
            .OrderBy(listener => listener.ProcessId)
            .ThenByDescending(listener => listener.Port)
            .Select(listener => new AntigravityProcessEndpoint(
                listener.Port,
                tokens.TryGetValue(listener.ProcessId, out var token) ? token : null))
            .ToArray();
    }

    public static IReadOnlyList<AntigravityListener> FindCandidateListeners()
    {
        var candidatePids = FindCandidateProcessIds();
        if (candidatePids.Count == 0) return Array.Empty<AntigravityListener>();

        return FindLoopbackListeners()
            .Where(listener => candidatePids.Contains(listener.ProcessId))
            .OrderBy(listener => listener.ProcessId)
            .ThenBy(listener => listener.Port)
            .ToArray();
    }

    /// <summary>
    /// Process names are read through the normal process API. No command line
    /// is needed for the runtime discovery path.
    /// </summary>
    public static IReadOnlySet<int> FindCandidateProcessIds()
    {
        var pids = new HashSet<int>();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (CandidateName.IsMatch(process.ProcessName))
                        {
                            pids.Add(process.Id);
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Process enumeration can be restricted; an empty candidate set
            // simply lets the Core source fall back to cli.log.
        }

        return pids;
    }

    /// <summary>
    /// Returns candidate process metadata for --discover. Command lines are
    /// redacted before they leave this method.
    /// </summary>
    public static IReadOnlyList<AntigravityProcessInfo> FindCandidateProcesses()
    {
        var candidatePids = FindCandidateProcessIds();
        if (candidatePids.Count == 0) return Array.Empty<AntigravityProcessInfo>();

        var names = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (candidatePids.Contains(process.Id))
                    {
                        names[process.Id] = process.ProcessName;
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        var commandLines = ReadCommandLines(candidatePids);
        return candidatePids
            .OrderBy(pid => pid)
            .Select(pid => new AntigravityProcessInfo(
                pid,
                names.TryGetValue(pid, out var name) ? name : "unknown",
                commandLines.TryGetValue(pid, out var commandLine)
                    ? RedactCommandLine(commandLine)
                    : "[erişilemedi]"))
            .ToArray();
    }

    public static IReadOnlyList<AntigravityListener> FindLoopbackListeners()
    {
        var rows = ReadListenerRows();
        return rows
            .Where(row => IsLoopback(row.LocalAddress))
            .Select(row => new AntigravityListener(
                unchecked((int)row.OwningPid),
                NetworkPort(row.LocalPort)))
            .Where(listener => listener.Port is >= 1 and <= 65535)
            .ToArray();
    }

    /// <summary>
    /// Süreç komut satırları (CSRF anahtarı oradadır). WMI (System.Management) yerine
    /// NtQueryInformationProcess(ProcessCommandLineInformation): WMI .NET'in yerleşik COM
    /// desteğini ister; WinUI'de kapalı, budanmış yayında da eksik üyeler yüzünden
    /// TypeInitializationException veriyordu ve uygulama her portta 401 alıyordu.
    /// Yerel çağrı COM gerektirmez, trimming'den etkilenmez ve çok daha hızlıdır.
    /// </summary>
    private static Dictionary<int, string> ReadCommandLines(IReadOnlySet<int> candidatePids)
    {
        var result = new Dictionary<int, string>();
        foreach (var pid in candidatePids)
        {
            if (ReadCommandLine(pid) is { } commandLine) result[pid] = commandLine;
        }

        if (result.Count < candidatePids.Count)
        {
            Ration.Core.Diagnostics.Trace.Error(
                "provider.source",
                $"provider=antigravity commandline-read partial read={result.Count} candidates={candidatePids.Count}");
        }

        return result;
    }

    private static string? ReadCommandLine(int pid)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        const int ProcessCommandLineInformation = 60;
        const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (status != StatusInfoLengthMismatch || length <= 0) return null;

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0) return null;

                // Tampon bir UNICODE_STRING ile başlar; Buffer alanı aynı tampon içini gösterir.
                var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                return text.Buffer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, IntPtr processInformation, int length, out int returnLength);

    private static IReadOnlyList<MibTcpRowOwnerPid> ReadListenerRows()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bufferLength = 0;
            var status = GetExtendedTcpTable(
                IntPtr.Zero,
                ref bufferLength,
                true,
                AddressFamilyIpv4,
                TcpTableOwnerPidListener,
                0);
            if (status != ErrorInsufficientBuffer || bufferLength <= 0)
            {
                return Array.Empty<MibTcpRowOwnerPid>();
            }

            var buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                status = GetExtendedTcpTable(
                    buffer,
                    ref bufferLength,
                    true,
                    AddressFamilyIpv4,
                    TcpTableOwnerPidListener,
                    0);
                if (status == ErrorInsufficientBuffer) continue;
                if (status != 0 || bufferLength < sizeof(int))
                {
                    return Array.Empty<MibTcpRowOwnerPid>();
                }

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                var rows = new List<MibTcpRowOwnerPid>(Math.Max(count, 0));

                for (var index = 0; index < count; index++)
                {
                    var rowPointer = IntPtr.Add(buffer, sizeof(int) + index * rowSize);
                    rows.Add(Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer));
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return Array.Empty<MibTcpRowOwnerPid>();
    }

    private static bool IsLoopback(uint address) =>
        address is 0x0100007F or 0x7F000001;

    private static int NetworkPort(uint value)
    {
        var port = (ushort)(value & 0xFFFF);
        return (ushort)((port >> 8) | (port << 8));
    }

    private static string RedactCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "(boş)";

        var redacted = SecretArgument.Replace(
            commandLine,
            match => match.Groups["prefix"].Value + "[gizlendi]");
        return SecretQueryValue.Replace(
            redacted,
            match => match.Groups["prefix"].Value + "[gizlendi]");
    }

    private static string? ExtractCsrfToken(string commandLine)
    {
        var token = ReadArgument(ExtensionCsrfArgument, commandLine);
        return string.IsNullOrWhiteSpace(token)
            ? ReadArgument(CsrfArgument, commandLine)
            : token;
    }

    private static string? ReadArgument(Regex argument, string commandLine)
    {
        var match = argument.Match(commandLine);
        if (!match.Success) return null;

        var value = match.Groups["value"].Value.Trim();
        return value.Trim('"');
    }
}
