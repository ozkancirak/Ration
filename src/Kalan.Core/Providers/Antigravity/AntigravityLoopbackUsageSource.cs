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
    private const string UserStatusEndpointPath =
        "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string HttpsErrorText = "Client sent an HTTP request to an HTTPS server";

    private readonly HttpClient _loopbackHttp;
    private readonly string _defaultLogPath;
    private readonly string? _overrideLogPath;
    private readonly Func<IReadOnlyList<int>> _findProcessPorts;
    private readonly Func<IReadOnlyList<AntigravityProcessEndpoint>> _findProcessEndpoints;
    private readonly Action<string> _rawResponseSink;
    private CachedEndpoint? _cachedEndpoint;
    private int _rawResponseLogged;

    private readonly record struct CachedEndpoint(
        int Port,
        string Scheme,
        string? CsrfToken);

    private readonly record struct PortCandidate(
        int Port,
        string Source,
        string? CsrfToken,
        string Scheme);

    private readonly record struct ProbeOutcome(
        bool HasResponse,
        bool ConnectionFailed,
        int? StatusCode,
        string Body,
        string Scheme);

    public AntigravityLoopbackUsageSource(
        HttpClient? http = null,
        string? defaultLogPath = null,
        string? overrideLogPath = null,
        Func<IReadOnlyList<int>>? findProcessPorts = null,
        Func<IReadOnlyList<AntigravityProcessEndpoint>>? findProcessEndpoints = null,
        Action<string>? rawResponseSink = null)
    {
        _loopbackHttp = http ?? CreateLoopbackClient();
        _defaultLogPath = defaultLogPath ?? KnownPaths.AntigravityDefaultCliLog;
        _overrideLogPath = overrideLogPath ?? (
            defaultLogPath is null ? KnownPaths.AntigravityOverrideCliLog : null);
        _findProcessPorts = findProcessPorts ?? (() => Array.Empty<int>());
        _findProcessEndpoints = findProcessEndpoints ?? (
            () => Array.Empty<AntigravityProcessEndpoint>());
        _rawResponseSink = rawResponseSink ?? (
            body => KalanTrace.RawResponse("provider.raw", body));
    }

    // Tur 7 test çağrı biçimini korur.
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

            var probe = await ProbeCandidateAsync(candidate, ct).ConfigureAwait(false);
            if (probe.ConnectionFailed)
            {
                connectionFailures++;
                KalanTrace.Info(
                    "provider.source",
                    $"provider=antigravity source=loopback candidate={candidate.Source} result=connection-failed");
                continue;
            }

            httpResponses++;
            KalanTrace.Info(
                "provider.source",
                $"provider=antigravity source=loopback candidate={candidate.Source} result=http-{probe.StatusCode} transport={probe.Scheme}");

            if (IsJson(probe.Body))
            {
                _cachedEndpoint = new CachedEndpoint(
                    candidate.Port,
                    probe.Scheme,
                    candidate.CsrfToken);
            }

            if (probe.StatusCode != (int)HttpStatusCode.OK) continue;

            // İlk HTTP 200, içerik bozuk olsa bile seçilen porttur.
            _cachedEndpoint = new CachedEndpoint(
                candidate.Port,
                probe.Scheme,
                candidate.CsrfToken);

            if (Interlocked.Exchange(ref _rawResponseLogged, 1) == 0)
            {
                _rawResponseSink(probe.Body);
            }

            AntigravityUsageParser.ParseResult parsed;
            try
            {
                parsed = AntigravityUsageParser.Parse(probe.Body);
            }
            catch (JsonException)
            {
                return Complete(
                    Snapshot.Empty("antigravity", ProviderStatus.Degraded,
                        "Antigravity kota yanıtı tanınmadı; veri yok.", Kind),
                    stopwatch,
                    $"degraded data=invalid port={candidate.Port} transport={probe.Scheme}");
            }

            var windows = parsed.Windows;

            if (windows.Count == 0)
            {
                return Complete(
                    Snapshot.Empty("antigravity", ProviderStatus.Degraded,
                        "Antigravity kota verisi yok.", Kind),
                    stopwatch,
                    $"degraded data=empty port={candidate.Port} transport={probe.Scheme}");
            }

            // Plan adı quotaInfo içinde değildir; ayrı çağrı yalnızca başlık
            // rozetini doldurur ve kota pencerelerine hiç dokunmaz.
            var planName = await FetchPlanNameAsync(
                candidate with { Scheme = probe.Scheme },
                ct).ConfigureAwait(false);

            return Complete(
                new UsageSnapshot(
                    ProviderId: "antigravity",
                    Windows: windows,
                    Credits: null,
                    Cost: null,
                    Status: ProviderStatus.Ok,
                    ResolvedVia: Kind,
                    FetchedAt: DateTimeOffset.UtcNow,
                    StaleReason: null,
                    PlanName: planName),
                stopwatch,
                $"ok windows={windows.Count} port={candidate.Port} transport={probe.Scheme}");
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

    private async Task<ProbeOutcome> ProbeCandidateAsync(
        PortCandidate candidate,
        CancellationToken ct)
    {
        try
        {
            var probe = await SendOnceAsync(candidate, ct).ConfigureAwait(false);
            if (candidate.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                probe.Body.Contains(HttpsErrorText, StringComparison.OrdinalIgnoreCase))
            {
                KalanTrace.Info(
                    "provider.source",
                    $"provider=antigravity source=loopback port={candidate.Port} result=tls-port");
                return await SendOnceAsync(
                    candidate with { Scheme = "https" },
                    ct).ConfigureAwait(false);
            }

            return probe;
        }
        catch (HttpRequestException)
        {
            return new ProbeOutcome(false, true, null, string.Empty, candidate.Scheme);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeOutcome(false, true, null, string.Empty, candidate.Scheme);
        }
    }

    private async Task<ProbeOutcome> SendOnceAsync(
        PortCandidate candidate,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{candidate.Scheme}://127.0.0.1:{candidate.Port}{EndpointPath}");
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        if (!string.IsNullOrWhiteSpace(candidate.CsrfToken))
        {
            request.Headers.TryAddWithoutValidation(
                "X-Codeium-Csrf-Token",
                candidate.CsrfToken);
        }

        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _loopbackHttp.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return new ProbeOutcome(
            HasResponse: true,
            ConnectionFailed: false,
            StatusCode: (int)response.StatusCode,
            Body: body,
            Scheme: candidate.Scheme);
    }

    private async Task<string?> FetchPlanNameAsync(
        PortCandidate candidate,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{candidate.Scheme}://127.0.0.1:{candidate.Port}{UserStatusEndpointPath}");
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        if (!string.IsNullOrWhiteSpace(candidate.CsrfToken))
        {
            request.Headers.TryAddWithoutValidation(
                "X-Codeium-Csrf-Token",
                candidate.CsrfToken);
        }

        request.Content = new StringContent(
            "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}",
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await _loopbackHttp.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            try
            {
                return AntigravityUsageParser.ParsePlanName(body);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private IReadOnlyList<PortCandidate> GetCandidates()
    {
        var candidates = new List<PortCandidate>();
        var indexes = new Dictionary<int, int>();
        string? processToken = null;

        void Add(PortCandidate candidate)
        {
            if (candidate.Port is < 1 or > 65535) return;
            processToken ??= candidate.CsrfToken;

            if (indexes.TryGetValue(candidate.Port, out var index))
            {
                var existing = candidates[index];
                if (existing.CsrfToken is null && candidate.CsrfToken is not null)
                {
                    candidates[index] = existing with { CsrfToken = candidate.CsrfToken };
                }

                return;
            }

            indexes[candidate.Port] = candidates.Count;
            candidates.Add(candidate);
        }

        if (_cachedEndpoint is { } cached)
        {
            Add(new PortCandidate(cached.Port, "cache", cached.CsrfToken, cached.Scheme));
        }

        try
        {
            foreach (var endpoint in _findProcessEndpoints())
            {
                Add(new PortCandidate(endpoint.Port, "process", endpoint.CsrfToken, "http"));
            }
        }
        catch
        {
            // A process metadata failure must not prevent the log fallback.
        }

        try
        {
            foreach (var port in _findProcessPorts())
            {
                Add(new PortCandidate(port, "process", processToken, "http"));
            }
        }
        catch
        {
            // A listener discovery failure must not prevent the log fallback.
        }

        AddLogCandidate(_defaultLogPath, "default-log", processToken, Add);
        if (_overrideLogPath is { } overrideLog &&
            !string.Equals(_defaultLogPath, overrideLog, StringComparison.OrdinalIgnoreCase))
        {
            AddLogCandidate(overrideLog, "override-log", processToken, Add);
        }

        return candidates;
    }

    private static void AddLogCandidate(
        string path,
        string source,
        string? csrfToken,
        Action<PortCandidate> add)
    {
        if (AntigravityPortFinder.FindNewestLogPort(path) is { } port)
        {
            add(new PortCandidate(port, source, csrfToken, "http"));
        }
    }

    private static bool IsJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind is
                JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpClient CreateLoopbackClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
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
