using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Kalan.Core.Abstractions;
using Kalan.Core.Model;
using KalanTrace = Kalan.Core.Diagnostics.Trace;

namespace Kalan.Core.Providers.Antigravity;

public sealed class AntigravityLoopbackUsageSource : IUsageSource
{
    private const string EndpointPath =
        "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";

    private readonly HttpClient _http;
    private readonly string _defaultLogPath;
    private readonly string? _overrideLogPath;
    private readonly Func<IReadOnlyList<int>> _findProcessPorts;
    private int _cachedPort;
    private int _rawResponseLogged;

    private readonly record struct PortCandidate(int Port, string Source);

    public AntigravityLoopbackUsageSource(
        HttpClient http,
        string? defaultLogPath = null,
        string? overrideLogPath = null,
        Func<IReadOnlyList<int>>? findProcessPorts = null)
    {
        _http = http;
        _defaultLogPath = defaultLogPath ?? KnownPaths.AntigravityDefaultCliLog;
        _overrideLogPath = overrideLogPath ?? (
            defaultLogPath is null ? KnownPaths.AntigravityOverrideCliLog : null);
        _findProcessPorts = findProcessPorts ?? (() => Array.Empty<int>());
    }

    // Tur 7 test çağrı biçimini korur; gerçek uygulama WMI'nin bütün portlarını
    // üstteki çoklu aday callback'iyle verir.
    public AntigravityLoopbackUsageSource(
        HttpClient http,
        string? logPath,
        Func<int?> findPort)
        : this(
            http,
            defaultLogPath: logPath,
            findProcessPorts: () => findPort() is { } port
                ? new[] { port }
                : Array.Empty<int>())
    {
    }

    public SourceKind Kind => SourceKind.Cli;

    // Kaynak yokken de FetchAsync çalışmalı; böylece UI "kurulu değil" ile
    // "veri yok" durumunu birbirinden ayırabilir.
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var candidates = GetCandidates();

        KalanTrace.Info(
            "provider.source",
            $"provider=antigravity source=loopback result=discovery candidates={candidates.Count}");

        if (candidates.Count == 0)
        {
            return Complete(
                Snapshot.Empty("antigravity", ProviderStatus.NotInstalled,
                    "Antigravity açık değil.", Kind),
                stopwatch,
                "not-installed candidates=0");
        }

        var connectionFailures = 0;
        var httpResponses = 0;

        foreach (var candidate in candidates)
        {
            KalanTrace.Info(
                "provider.source",
                $"provider=antigravity source=loopback candidate={candidate.Source} probe=port-{candidate.Port}");

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"http://127.0.0.1:{candidate.Port}{EndpointPath}");
                request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    httpResponses++;
                    KalanTrace.Info(
                        "provider.source",
                        $"provider=antigravity source=loopback candidate={candidate.Source} result=http-{(int)response.StatusCode}");
                    continue;
                }

                // İlk HTTP 200, içerik bozuk olsa bile seçilen porttur.
                _cachedPort = candidate.Port;
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _rawResponseLogged, 1) == 0)
                {
                    KalanTrace.RawResponse("provider.raw", body);
                }

                IReadOnlyList<UsageWindow> windows;
                try
                {
                    windows = AntigravityUsageParser.ParseWindows(body);
                }
                catch (JsonException)
                {
                    return Complete(
                        Snapshot.Empty("antigravity", ProviderStatus.Degraded,
                            "Antigravity kota yanıtı tanınmadı; veri yok.", Kind),
                        stopwatch,
                        $"degraded data=invalid port={candidate.Port}");
                }

                if (windows.Count == 0)
                {
                    return Complete(
                        Snapshot.Empty("antigravity", ProviderStatus.Degraded,
                            "Antigravity kota verisi yok.", Kind),
                        stopwatch,
                        $"degraded data=empty port={candidate.Port}");
                }

                return Complete(
                    new UsageSnapshot(
                        ProviderId: "antigravity",
                        Windows: windows,
                        Credits: null,
                        Cost: null,
                        Status: ProviderStatus.Ok,
                        ResolvedVia: Kind,
                        FetchedAt: DateTimeOffset.UtcNow,
                        StaleReason: null),
                    stopwatch,
                    $"ok windows={windows.Count} port={candidate.Port}");
            }
            catch (HttpRequestException)
            {
                connectionFailures++;
                KalanTrace.Info(
                    "provider.source",
                    $"provider=antigravity source=loopback candidate={candidate.Source} result=connection-refused");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                connectionFailures++;
                KalanTrace.Info(
                    "provider.source",
                    $"provider=antigravity source=loopback candidate={candidate.Source} result=timeout");
            }
            catch (OperationCanceledException)
            {
                return Complete(
                    Snapshot.Empty("antigravity", ProviderStatus.Error,
                        "Antigravity isteği iptal edildi.", Kind),
                    stopwatch,
                    "error cancelled");
            }
            catch (Exception ex)
            {
                httpResponses++;
                KalanTrace.Info(
                    "provider.source",
                    $"provider=antigravity source=loopback candidate={candidate.Source} result=error type={ex.GetType().Name}");
            }
        }

        if (connectionFailures == candidates.Count && httpResponses == 0)
        {
            return Complete(
                Snapshot.Empty("antigravity", ProviderStatus.NotInstalled,
                    "Antigravity dil sunucusuna ulaşılamadı.", Kind),
                stopwatch,
                $"not-installed connection-failed candidates={candidates.Count}");
        }

        return Complete(
            Snapshot.Empty("antigravity", ProviderStatus.Degraded,
                "Antigravity dil sunucusu kota endpoint'ine geçerli yanıt vermedi.", Kind),
            stopwatch,
            $"degraded no-200 candidates={candidates.Count}");
    }

    private IReadOnlyList<PortCandidate> GetCandidates()
    {
        var candidates = new List<PortCandidate>();
        var seen = new HashSet<int>();

        void Add(int port, string source)
        {
            if (port is < 1 or > 65535 || !seen.Add(port)) return;
            candidates.Add(new PortCandidate(port, source));
        }

        if (_cachedPort != 0) Add(_cachedPort, "cache");

        try
        {
            foreach (var port in _findProcessPorts()) Add(port, "process");
        }
        catch
        {
            // A WMI query failure must not prevent the log sources.
        }

        AddLogCandidate(_defaultLogPath, "default-log", Add);
        if (_overrideLogPath is { } overrideLog &&
            !string.Equals(_defaultLogPath, overrideLog, StringComparison.OrdinalIgnoreCase))
        {
            AddLogCandidate(overrideLog, "override-log", Add);
        }

        return candidates;
    }

    private static void AddLogCandidate(
        string path,
        string source,
        Action<int, string> add)
    {
        if (AntigravityPortFinder.FindNewestLogPort(path) is { } port)
        {
            add(port, source);
        }
    }

    private static UsageSnapshot Complete(
        UsageSnapshot snapshot,
        Stopwatch stopwatch,
        string result)
    {
        KalanTrace.Info(
            "provider.source",
            $"provider=antigravity source=loopback result={result} status={snapshot.Status} durationMs={stopwatch.ElapsedMilliseconds}");
        return snapshot;
    }
}
