using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Ration.Core.Abstractions;
using Ration.Core.Cost;
using Ration.Core.Model;
using RationTrace = Ration.Core.Diagnostics.Trace;

namespace Ration.Core.Providers.Antigravity;

public sealed class AntigravityLoopbackUsageSource : IProgressiveUsageSource
{
    private const int MaxTrajectoryCount = 100;
    private static readonly TimeSpan TokenWorkTimeout = TimeSpan.FromSeconds(10);

    private const string EndpointPath =
        "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    private const string UserStatusEndpointPath =
        "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string AllCascadeTrajectoriesEndpointPath =
        "/exa.language_server_pb.LanguageServerService/GetAllCascadeTrajectories";
    private const string CascadeTrajectoryMetadataEndpointPath =
        "/exa.language_server_pb.LanguageServerService/GetCascadeTrajectoryGeneratorMetadata";
    private const string HttpsErrorText = "Client sent an HTTP request to an HTTPS server";

    private readonly HttpClient _loopbackHttp;
    private readonly string _defaultLogPath;
    private readonly string? _overrideLogPath;
    private readonly Func<IReadOnlyList<int>> _findProcessPorts;
    private readonly Func<IReadOnlyList<AntigravityProcessEndpoint>> _findProcessEndpoints;
    private readonly Action<string> _rawResponseSink;
    private CachedEndpoint? _cachedEndpoint;
    private int _rawResponseLogged;

    public event Action<UsageSnapshot>? SnapshotUpdated;

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
            body => RationTrace.RawResponse("provider.raw", body));
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

        RationTrace.Info(
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
            RationTrace.Info(
                "provider.source",
                $"provider=antigravity source=loopback candidate={candidate.Source} probe=port-{candidate.Port}");

            var probe = await ProbeCandidateAsync(candidate, ct).ConfigureAwait(false);
            if (probe.ConnectionFailed)
            {
                connectionFailures++;
                RationTrace.Info(
                    "provider.source",
                    $"provider=antigravity source=loopback candidate={candidate.Source} result=connection-failed");
                continue;
            }

            httpResponses++;
            RationTrace.Info(
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
                parsed = new AntigravityUsageParser.ParseResult(Array.Empty<UsageWindow>(), null);
            }

            var windows = parsed.Windows;
            var resolvedCandidate = candidate with { Scheme = probe.Scheme };

            // Kota ve token kullanımı iki bağımsız kaynaktır. Kota hazır olur olmaz
            // yayınla; token taraması bunu geciktiremez ve boş dönerek kotayı silemez.
            SnapshotUpdated?.Invoke(new UsageSnapshot(
                ProviderId: "antigravity",
                Windows: windows,
                Credits: null,
                Cost: null,
                Status: windows.Count > 0 ? ProviderStatus.Ok : ProviderStatus.Degraded,
                ResolvedVia: Kind,
                FetchedAt: DateTimeOffset.UtcNow,
                StaleReason: windows.Count == 0 ? "Antigravity kota verisi yok." : null));
            RationTrace.Info(
                "provider.source",
                $"provider=antigravity source=quota result=published windows={windows.Count}");

            CostReport? tokenUsage;
            string? planName;
            using (var tokenBudget = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                tokenBudget.CancelAfter(TokenWorkTimeout);
                var tokenTask = FetchTokenUsageAsync(
                    resolvedCandidate,
                    tokenBudget.Token,
                    ct);
                var planTask = FetchPlanNameAsync(
                    resolvedCandidate,
                    tokenBudget.Token,
                    ct);

                await Task.WhenAll(tokenTask, planTask).ConfigureAwait(false);
                tokenUsage = tokenTask.Result;
                planName = planTask.Result;

                if (tokenBudget.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    RationTrace.Info(
                        "provider.source",
                        $"provider=antigravity source=trajectory result=budget-exceeded limitSeconds={(int)TokenWorkTimeout.TotalSeconds}");
                }
            }

            var status = windows.Count > 0 || tokenUsage is not null
                ? ProviderStatus.Ok
                : ProviderStatus.Degraded;
            return Complete(
                new UsageSnapshot(
                    ProviderId: "antigravity",
                    Windows: windows,
                    Credits: null,
                    Cost: tokenUsage,
                    Status: status,
                    ResolvedVia: Kind,
                    FetchedAt: DateTimeOffset.UtcNow,
                    StaleReason: windows.Count == 0
                        ? "Antigravity kota verisi yok."
                        : null,
                    PlanName: planName),
                stopwatch,
                $"{(status == ProviderStatus.Ok ? "ok" : "degraded")} windows={windows.Count} "
                + $"tokenData={(tokenUsage is null ? "none" : "yes")} "
                + $"port={candidate.Port} transport={probe.Scheme}");
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
                RationTrace.Info(
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
        => await SendRpcAsync(candidate, EndpointPath, "{}", ct).ConfigureAwait(false);

    private async Task<ProbeOutcome> SendRpcAsync(
        PortCandidate candidate,
        string endpointPath,
        string body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{candidate.Scheme}://127.0.0.1:{candidate.Port}{endpointPath}");
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        if (!string.IsNullOrWhiteSpace(candidate.CsrfToken))
        {
            request.Headers.TryAddWithoutValidation(
                "X-Codeium-Csrf-Token",
                candidate.CsrfToken);
        }

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _loopbackHttp.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return new ProbeOutcome(
            HasResponse: true,
            ConnectionFailed: false,
            StatusCode: (int)response.StatusCode,
            Body: responseBody,
            Scheme: candidate.Scheme);
    }

    private async Task<string?> FetchPlanNameAsync(
        PortCandidate candidate,
        CancellationToken ct,
        CancellationToken callerCt)
    {
        try
        {
            var response = await SendRpcAsync(
                candidate,
                UserStatusEndpointPath,
                "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}",
                ct).ConfigureAwait(false);
            if (response.StatusCode != (int)HttpStatusCode.OK) return null;

            try
            {
                return AntigravityUsageParser.ParsePlanName(response.Body);
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
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<CostReport?> FetchTokenUsageAsync(
        PortCandidate candidate,
        CancellationToken ct,
        CancellationToken callerCt)
    {
        var tally = new TokenTally();
        try
        {
            var summaries = await SendRpcAsync(
                candidate,
                AllCascadeTrajectoriesEndpointPath,
                "{}",
                ct).ConfigureAwait(false);
            if (summaries.StatusCode != (int)HttpStatusCode.OK)
            {
                RationTrace.Info(
                    "provider.source",
                    $"provider=antigravity source=trajectory result=summaries-http-{summaries.StatusCode}");
                return null;
            }

            if (!AntigravityTokenParser.TryReadCascadeIds(summaries.Body, out var cascadeIds))
            {
                RationTrace.Info(
                    "provider.source",
                    "provider=antigravity source=trajectory result=summaries-invalid-json");
                return null;
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            if (cascadeIds.Count > MaxTrajectoryCount)
            {
                RationTrace.Info(
                    "provider.source",
                    $"provider=antigravity source=trajectory result=limited total={cascadeIds.Count} limit={MaxTrajectoryCount}");
                cascadeIds = cascadeIds.Take(MaxTrajectoryCount).ToArray();
            }

            foreach (var cascadeId in cascadeIds)
            {
                var body = JsonSerializer.Serialize(new
                {
                    cascadeId,
                    generatorMetadataOffset = 0,
                    includeMessages = false,
                });
                var metadata = await SendRpcAsync(
                    candidate,
                    CascadeTrajectoryMetadataEndpointPath,
                    body,
                    ct).ConfigureAwait(false);

                if (metadata.StatusCode != (int)HttpStatusCode.OK)
                {
                    RationTrace.Info(
                        "provider.source",
                        $"provider=antigravity source=trajectory result=metadata-http-{metadata.StatusCode}");
                    continue;
                }

                var accepted = AntigravityTokenParser.AddGeneratorMetadataToTally(
                    metadata.Body,
                    tally,
                    seenIds,
                    out var skipped,
                    out var missingRequired);
                if (missingRequired > 0)
                {
                    RationTrace.Info(
                        "provider.source",
                        $"provider=antigravity source=trajectory result=records-skipped-missing-required count={missingRequired}");
                }

                if (skipped > 0)
                {
                    RationTrace.Info(
                        "provider.source",
                        $"provider=antigravity source=trajectory result=records-skipped count={skipped}");
                }

                if (accepted == 0 && skipped == 0 && metadata.Body.Length > 0)
                {
                    RationTrace.Info(
                        "provider.source",
                        "provider=antigravity source=trajectory result=metadata-empty");
                }
            }

            return BuildCostReport(tally);
        }
        catch (HttpRequestException)
        {
            RationTrace.Info(
                "provider.source",
                "provider=antigravity source=trajectory result=connection-failed");
            return null;
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            RationTrace.Info(
                "provider.source",
                $"provider=antigravity source=trajectory result=timeout entries={tally.EntryCount}");
            return BuildCostReport(tally);
        }
    }

    private static CostReport? BuildCostReport(TokenTally tally)
    {
        if (tally.EntryCount == 0) return null;

        var fetchedAt = DateTimeOffset.UtcNow;
        return new CostReport(
            TotalCost: 0,
            Currency: "USD",
            PeriodStart: fetchedAt.AddDays(-30),
            PeriodEnd: fetchedAt,
            InputTokens: tally.TotalInputTokens,
            OutputTokens: tally.TotalOutputTokens,
            CacheReadTokens: tally.TotalCacheReadTokens,
            CacheCreationTokens: tally.TotalCacheCreationTokens,
            ReasoningTokens: tally.TotalReasoningTokens,
            Models: tally.Models
                .Select(model => new ModelTokenUsage(
                    model.Model,
                    model.TotalTokens,
                    model.InputTokens,
                    model.OutputTokens,
                    model.CacheReadTokens,
                    model.CacheCreationTokens))
                .ToArray());
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
        RationTrace.Info(
            "provider.source",
            $"provider=antigravity source=loopback result={result} status={snapshot.Status} durationMs={stopwatch.ElapsedMilliseconds}");
        return snapshot;
    }
}
