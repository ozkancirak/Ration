using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ration.Core.Abstractions;
using Ration.Core.Model;
using Ration.Core.Providers;
using RationTrace = Ration.Core.Diagnostics.Trace;

namespace Ration.Core.Providers.Claude;

/// <summary>
/// Yerel dosyadaki OAuth token ile Anthropic kota uç noktasını sorgular.
/// Kind = LocalFile'dır: kimlik yerel dosyadan gelir (AGENTS.md kaynak zinciri, 1. sıra).
/// </summary>
public sealed class ClaudeOAuthUsageSource : IUsageSource
{
    public const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    public const string OAuthBetaHeader = "oauth-2025-04-20";

    private readonly HttpClient _http;
    private readonly Func<ClaudeCredentials?> _credentials;
    private readonly string _retryAfterFile;

    public SourceKind Kind => SourceKind.LocalFile;

    /// <summary>
    /// Tanı amaçlı son ham yanıt (<c>ration usage --raw</c>).
    /// Kota verisidir; token içermez.
    /// </summary>
    public string? LastRawResponse { get; private set; }

    public int? LastStatusCode { get; private set; }

    public string? LastRetryAfter { get; private set; }

    public bool LastCredentialsAvailable { get; private set; }

    public bool LastCredentialsExpired { get; private set; }

    public bool LastCredentialsHasRefreshToken { get; private set; }

    public DateTimeOffset? LastCredentialsExpiresAt { get; private set; }

    public ClaudeOAuthUsageSource(
        HttpClient http,
        Func<ClaudeCredentials?>? credentials = null,
        string? retryAfterFile = null)
    {
        _http = http;
        _credentials = credentials ?? (() => ClaudeCredentialStore.TryRead());
        _retryAfterFile = retryAfterFile ?? Path.Combine(KnownPaths.CacheDir, "claude-retry-after.txt");
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(_credentials() is not null);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        ResetDiagnostics();

        var credentials = _credentials();
        SetCredentialDiagnostics(credentials);

        if (credentials is null)
        {
            return Snapshot.Empty("claude", ProviderStatus.AuthRequired,
                L.T("~/.claude/.credentials.json not found or has no claudeAiOauth.", "~/.claude/.credentials.json bulunamadı ya da claudeAiOauth içermiyor."), Kind);
        }

        // 429 sonrası sunucunun istediği süre dolmadan tekrar sorma; bekleme zamanı diskte
        // tutulur ki uygulama yeniden başlatılınca da uyulsun.
        if (ReadRetryAfter() is { } until && until > DateTimeOffset.UtcNow)
        {
            RationTrace.Info("provider.http", $"provider=claude skipped=retry-after until={until:O}");
            return RateLimited(until);
        }

        return await FetchUsageAsync(credentials, ct).ConfigureAwait(false);
    }

    private UsageSnapshot RateLimited(DateTimeOffset? until) =>
        Snapshot.Empty("claude", ProviderStatus.Error,
            until is { } u
                ? L.T($"Claude rate limit hit — will retry after {u.ToLocalTime():t}", $"Claude hız sınırına takıldı — {u.ToLocalTime():HH:mm} sonrasında tekrar denenecek")
                : L.T("Claude rate limit hit — will retry shortly", "Claude hız sınırına takıldı — biraz sonra tekrar denenecek"),
            Kind);

    private DateTimeOffset? ReadRetryAfter()
    {
        try
        {
            return File.Exists(_retryAfterFile) &&
                   DateTimeOffset.TryParse(File.ReadAllText(_retryAfterFile), out var until)
                ? until
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void WriteRetryAfter(DateTimeOffset until)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_retryAfterFile)!);
            File.WriteAllText(_retryAfterFile, until.ToString("O"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static DateTimeOffset? RetryAfterUntil(RetryConditionHeaderValue? header) =>
        header?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : header?.Date;

    private async Task<UsageSnapshot> FetchUsageAsync(ClaudeCredentials credentials, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);

        RationTrace.Info(
            "provider.http",
            "provider=claude request-method=GET request-url=" + UsageEndpoint +
            " request-header-names=Authorization,anthropic-beta");

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LastStatusCode = (int)response.StatusCode;
            LastRetryAfter = response.Headers.TryGetValues("Retry-After", out var retryAfter)
                ? string.Join(", ", retryAfter)
                : null;
            LastRawResponse = body;
            var redactedPreview = CompactPreview(RawResponseRedactor.Redact(body));

            RationTrace.Info(
                "provider.http",
                $"provider=claude raw-status-code={(int)response.StatusCode}");
            RationTrace.Info(
                "provider.http",
                $"provider=claude response-body-first200={redactedPreview}");
            RationTrace.Info(
                "provider.http",
                $"provider=claude retry-after={(LastRetryAfter is null ? "none" : $"set value={LastRetryAfter}")}");
            RationTrace.Info(
                "provider.http",
                $"provider=claude endpoint=usage status={(int)response.StatusCode}");

            if ((int)response.StatusCode == 429)
            {
                RationTrace.Info("provider.http", "provider=claude mapped-status=Error");
                var until = RetryAfterUntil(response.Headers.RetryAfter);
                if (until is not null) WriteRetryAfter(until.Value);
                return RateLimited(until);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var reason = response.StatusCode == HttpStatusCode.Unauthorized && credentials.IsExpired
                    ? L.T("Session needs renewal — run Claude Code once", "Oturum yenilenmeli — Claude Code'u bir kez çalıştır")
                    : L.T($"Token rejected (HTTP {(int)response.StatusCode}). Sign in again with the Claude Code CLI.", $"Token reddedildi (HTTP {(int)response.StatusCode}). Claude Code CLI ile tekrar giriş yapın.");

                RationTrace.Info("provider.http", "provider=claude mapped-status=AuthRequired");
                return Snapshot.Empty("claude", ProviderStatus.AuthRequired, reason, Kind);
            }

            if (!response.IsSuccessStatusCode)
            {
                RationTrace.Info("provider.http", "provider=claude mapped-status=Error");
                return Snapshot.Empty("claude", ProviderStatus.Error,
                    L.T($"Unexpected response: HTTP {(int)response.StatusCode}", $"Beklenmedik yanıt: HTTP {(int)response.StatusCode}"), Kind);
            }

            var windows = ClaudeUsageParser.ParseWindows(body);
            var status = windows.Count > 0 ? ProviderStatus.Ok : ProviderStatus.Degraded;
            RationTrace.Info("provider.http", $"provider=claude mapped-status={status}");

            return new UsageSnapshot(
                ProviderId: "claude",
                Windows: windows,
                Credits: null,
                Cost: null,
                Status: status,
                ResolvedVia: Kind,
                FetchedAt: DateTimeOffset.UtcNow,
                StaleReason: windows.Count > 0
                    ? null
                    : L.T("Response received but no known window. Check the schema with 'ration usage -p claude --raw'.", "Yanıt alındı ama tanınan pencere yok. 'ration usage -p claude --raw' ile şemayı kontrol edin."),
                PlanName: credentials.SubscriptionType);
        }
        catch (JsonException ex)
        {
            RationTrace.Error("provider.http", $"provider=claude endpoint=usage error={ex.GetType().Name}");
            RationTrace.Info("provider.http", "provider=claude mapped-status=Error");
            return Snapshot.Empty("claude", ProviderStatus.Error,
                L.T($"JSON could not be parsed: {ex.Message}", $"JSON ayrıştırılamadı: {ex.Message}"), Kind);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            RationTrace.Error("provider.http", $"provider=claude endpoint=usage error={ex.GetType().Name}");
            RationTrace.Info("provider.http", "provider=claude mapped-status=Error");
            return Snapshot.Empty("claude", ProviderStatus.Error,
                L.T($"Network error: {ex.GetType().Name}", $"Ağ hatası: {ex.GetType().Name}"), Kind);
        }
    }

    private static string CompactPreview(string value)
    {
        var compact = string.Join(
            ' ',
            value.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 200 ? compact : compact[..200];
    }

    private void ResetDiagnostics()
    {
        LastRawResponse = null;
        LastStatusCode = null;
        LastRetryAfter = null;
        LastCredentialsAvailable = false;
        LastCredentialsExpired = false;
        LastCredentialsHasRefreshToken = false;
        LastCredentialsExpiresAt = null;
    }

    private void SetCredentialDiagnostics(ClaudeCredentials? credentials)
    {
        LastCredentialsAvailable = credentials is not null;
        LastCredentialsExpired = credentials?.IsExpired == true;
        LastCredentialsHasRefreshToken = !string.IsNullOrWhiteSpace(credentials?.RefreshToken);
        LastCredentialsExpiresAt = credentials?.ExpiresAt;
    }

}

/// <summary>
/// Yanıt şeması resmî olarak belgelenmediği için ayrıştırma KASITLI olarak toleranslıdır:
/// bir pencere düz sayı da olabilir, { utilization, resets_at } nesnesi de.
/// Tanınmayan alan sessizce atlanır — eksik veri gösteririz, uydurma veri üretmeyiz.
/// </summary>
public static class ClaudeUsageParser
{
    // Pencere uzunluğu anahtardan türetilir: five_hour → 5 saat,
    // seven_day* → 7 gün. Yanıt uzunluğu taşımaz, anahtar taşır.
    private static readonly (string Key, WindowKind Kind, string Label, TimeSpan Length)[] KnownWindows =
    {
        ("five_hour",        WindowKind.Session, L.T("5-hour", "5 saatlik"),       TimeSpan.FromHours(5)),
        ("seven_day",        WindowKind.Weekly,  L.T("Weekly", "Haftalık"),        TimeSpan.FromDays(7)),
        ("seven_day_opus",   WindowKind.Weekly,  L.T("Weekly · Opus", "Haftalık · Opus"), TimeSpan.FromDays(7)),
        ("seven_day_sonnet", WindowKind.Weekly,  L.T("Weekly · Sonnet", "Haftalık · Sonnet"), TimeSpan.FromDays(7)),
    };

    private static readonly string[] PercentNames = { "utilization", "used_percent", "percent", "usage" };

    private static readonly string[] ResetNames = { "resets_at", "reset_at", "resetsAt" };

    public static IReadOnlyList<UsageWindow> ParseWindows(string json)
    {
        var result = new List<UsageWindow>();

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

        foreach (var (key, kind, label, length) in KnownWindows)
        {
            if (!doc.RootElement.TryGetProperty(key, out var element)) continue;

            var window = ReadWindow(element, kind, label, length);
            if (window is not null) result.Add(window);
        }

        return result;
    }

    private static UsageWindow? ReadWindow(JsonElement element, WindowKind kind, string label, TimeSpan length)
    {
        double? percent = null;
        DateTimeOffset? resetsAt = null;

        if (element.ValueKind == JsonValueKind.Number)
        {
            percent = element.GetDouble();
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            percent = JsonHelpers.ReadFirstNumber(element, PercentNames);

            foreach (var name in ResetNames)
            {
                if (!element.TryGetProperty(name, out var resetElement)) continue;

                resetsAt = JsonHelpers.ReadTimestamp(resetElement);
                if (resetsAt is not null) break;
            }
        }

        if (percent is null) return null;

        var clamped = Math.Clamp(percent.Value, 0, 100);
        return new UsageWindow(kind, clamped, 100, clamped, resetsAt, label, length);
    }
}
