using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kalan.Core.Abstractions;
using Kalan.Core.Diagnostics;
using Kalan.Core.Model;
using KalanTrace = Kalan.Core.Diagnostics.Trace;

namespace Kalan.Core.Providers.Antigravity;

public sealed class AntigravityLoopbackUsageSource : IUsageSource
{
    private const string EndpointPath =
        "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";

    private readonly HttpClient _http;
    private readonly string _logPath;
    private readonly Func<int?> _findPort;

    public AntigravityLoopbackUsageSource(
        HttpClient http,
        string? logPath = null,
        Func<int?>? findPort = null)
    {
        _http = http;
        _logPath = logPath ?? KnownPaths.AntigravityCliLog;
        _findPort = findPort ?? (() => AntigravityPortFinder.FindPort(_logPath));
    }

    public SourceKind Kind => SourceKind.Cli;

    // Kaynak yokken de FetchAsync çalışmalı; böylece UI "kurulu değil" ile
    // "veri yok" durumunu birbirinden ayırabilir.
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        KalanTrace.Info("provider.source", "provider=antigravity source=loopback path=antigravity-cli/cli.log start");

        int? port;
        try
        {
            port = _findPort();
        }
        catch
        {
            port = null;
        }

        if (port is null)
        {
            return Complete(
                Snapshot.Empty("antigravity", ProviderStatus.NotInstalled,
                    "Antigravity açık değil.", Kind),
                stopwatch,
                "not-installed port=none");
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://127.0.0.1:{port.Value}{EndpointPath}");
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Complete(
                    Snapshot.Empty("antigravity", ProviderStatus.Degraded,
                        $"Antigravity kota sunucusu HTTP {(int)response.StatusCode} döndürdü.", Kind),
                    stopwatch,
                    $"degraded http={(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var windows = AntigravityUsageParser.ParseWindows(body);
            if (windows.Count == 0)
            {
                return Complete(
                    Snapshot.Empty("antigravity", ProviderStatus.Degraded,
                        "Antigravity kota verisi yok.", Kind),
                    stopwatch,
                    "degraded data=empty");
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
                $"ok windows={windows.Count}");
        }
        catch (HttpRequestException)
        {
            return Complete(
                Snapshot.Empty("antigravity", ProviderStatus.NotInstalled,
                    "Antigravity açık değil.", Kind),
                stopwatch,
                "not-installed connection-refused");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Complete(
                Snapshot.Empty("antigravity", ProviderStatus.NotInstalled,
                    "Antigravity açık değil.", Kind),
                stopwatch,
                "not-installed timeout");
        }
        catch (JsonException)
        {
            return Complete(
                Snapshot.Empty("antigravity", ProviderStatus.Degraded,
                    "Antigravity kota yanıtı tanınmadı; veri yok.", Kind),
                stopwatch,
                "degraded data=invalid");
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
            return Complete(
                Snapshot.Empty("antigravity", ProviderStatus.Error,
                    $"Antigravity kaynağı okunamadı: {ex.GetType().Name}.", Kind),
                stopwatch,
                $"error type={ex.GetType().Name}");
        }
    }

    private static UsageSnapshot Complete(
        UsageSnapshot snapshot,
        Stopwatch stopwatch,
        string result)
    {
        KalanTrace.Info(
            "provider.source",
            $"provider=antigravity source=loopback path=antigravity-cli/cli.log result={result} status={snapshot.Status} durationMs={stopwatch.ElapsedMilliseconds}");
        return snapshot;
    }
}
