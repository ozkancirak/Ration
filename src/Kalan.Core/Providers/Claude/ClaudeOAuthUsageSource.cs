using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kalan.Core.Abstractions;
using Kalan.Core.Model;

namespace Kalan.Core.Providers.Claude;

/// <summary>
/// Yerel dosyadaki OAuth token ile Anthropic kota uç noktasını sorgular.
/// Kind = LocalFile'dır: kimlik yerel dosyadan gelir (AGENTS.md kaynak zinciri, 1. sıra).
/// </summary>
public sealed class ClaudeOAuthUsageSource : IUsageSource
{
    public const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    public const string OAuthBetaHeader = "oauth-2025-04-20";
    public const string RefreshEndpoint = "https://console.anthropic.com/v1/oauth/token";
    public const string RefreshClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string RefreshScope =
        "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    private readonly HttpClient _http;
    private readonly Func<ClaudeCredentials?> _sourceCredentials;
    private readonly Func<ClaudeCredentials?> _credentials;
    private readonly Func<ClaudeCredentials, bool> _saveRefreshedCredentials;

    public SourceKind Kind => SourceKind.LocalFile;

    /// <summary>
    /// Tanı amaçlı son ham yanıt (<c>kalan usage --raw</c>).
    /// Kota verisidir; token içermez.
    /// </summary>
    public string? LastRawResponse { get; private set; }

    public int? LastStatusCode { get; private set; }

    public string? LastRetryAfter { get; private set; }

    public bool LastCredentialsAvailable { get; private set; }

    public bool LastCredentialsExpired { get; private set; }

    public bool LastCredentialsHasRefreshToken { get; private set; }

    public DateTimeOffset? LastCredentialsExpiresAt { get; private set; }

    public bool LastRefreshAttempted { get; private set; }

    public int? LastRefreshStatusCode { get; private set; }

    public bool LastRefreshCacheWritten { get; private set; }

    public string? LastRefreshError { get; private set; }

    public ClaudeOAuthUsageSource(
        HttpClient http,
        Func<ClaudeCredentials?>? credentials = null,
        Func<ClaudeCredentials, bool>? saveRefreshedCredentials = null)
    {
        _http = http;
        _sourceCredentials = credentials ?? (() => ClaudeCredentialStore.TryRead());
        _credentials = credentials ?? (() => ClaudeCredentialStore.TryReadEffective());
        _saveRefreshedCredentials = saveRefreshedCredentials ?? ClaudeCredentialStore.TryWriteCache;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(_credentials() is not null);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        ResetDiagnostics();

        var sourceCredentials = _sourceCredentials();
        var credentials = _credentials();
        SetCredentialDiagnostics(sourceCredentials);

        if (credentials is null)
        {
            return Snapshot.Empty("claude", ProviderStatus.AuthRequired,
                "~/.claude/.credentials.json bulunamadı ya da claudeAiOauth içermiyor.", Kind);
        }

        if (credentials.IsExpired && !string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            var refreshed = await TryRefreshAsync(credentials, ct).ConfigureAwait(false);
            if (refreshed is not null)
            {
                credentials = refreshed;
            }
            else if (LastRefreshStatusCode is >= 400 and < 500)
            {
                return Snapshot.Empty("claude", ProviderStatus.AuthRequired,
                    "Claude OAuth yenilemesi reddedildi. Claude Code CLI ile tekrar giriş yapın.", Kind);
            }
        }

        return await FetchUsageAsync(credentials, ct).ConfigureAwait(false);
    }

    private async Task<UsageSnapshot> FetchUsageAsync(ClaudeCredentials credentials, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LastStatusCode = (int)response.StatusCode;
            LastRetryAfter = response.Headers.TryGetValues("Retry-After", out var retryAfter)
                ? string.Join(", ", retryAfter)
                : null;
            LastRawResponse = body;

            if ((int)response.StatusCode == 429)
            {
                return Snapshot.Empty("claude", ProviderStatus.Error,
                    "Hız sınırı (HTTP 429): Çok fazla istek yapıldı, biraz sonra tekrar denenecek.", Kind);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var reason = credentials.IsExpired
                    ? "OAuth token süresi dolmuş ve sunucu reddetti. Claude Code CLI ile tekrar giriş yapın."
                    : $"Token reddedildi (HTTP {(int)response.StatusCode}). Claude Code CLI ile tekrar giriş yapın.";

                return Snapshot.Empty("claude", ProviderStatus.AuthRequired, reason, Kind);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Snapshot.Empty("claude", ProviderStatus.Error,
                    $"Beklenmedik yanıt: HTTP {(int)response.StatusCode}", Kind);
            }

            var windows = ClaudeUsageParser.ParseWindows(body);

            return new UsageSnapshot(
                ProviderId: "claude",
                Windows: windows,
                Credits: null,
                Cost: null,
                Status: windows.Count > 0 ? ProviderStatus.Ok : ProviderStatus.Degraded,
                ResolvedVia: Kind,
                FetchedAt: DateTimeOffset.UtcNow,
                StaleReason: windows.Count > 0
                    ? null
                    : "Yanıt alındı ama tanınan pencere yok. 'kalan usage -p claude --raw' ile şemayı kontrol edin.",
                PlanName: credentials.SubscriptionType);
        }
        catch (JsonException ex)
        {
            return Snapshot.Empty("claude", ProviderStatus.Error,
                $"JSON ayrıştırılamadı: {ex.Message}", Kind);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Snapshot.Empty("claude", ProviderStatus.Error,
                $"Ağ hatası: {ex.GetType().Name}", Kind);
        }
    }

    private async Task<ClaudeCredentials?> TryRefreshAsync(
        ClaudeCredentials current,
        CancellationToken ct)
    {
        LastRefreshAttempted = true;

        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    grant_type = "refresh_token",
                    refresh_token = current.RefreshToken,
                    client_id = RefreshClientId,
                    scope = RefreshScope,
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            LastRefreshStatusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LastRefreshError = $"HTTP {(int)response.StatusCode}";
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var accessToken = ReadString(root, "access_token", "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                LastRefreshError = "Yanıtta access_token yok";
                return null;
            }

            DateTimeOffset? expiresAt = null;
            foreach (var name in new[] { "expires_at", "expiresAt" })
            {
                if (!root.TryGetProperty(name, out var expiry)) continue;
                expiresAt = JsonHelpers.ReadTimestamp(expiry);
                if (expiresAt is not null) break;
            }

            if (expiresAt is null && root.TryGetProperty("expires_in", out var expiresIn) &&
                expiresIn.ValueKind == JsonValueKind.Number &&
                expiresIn.TryGetDouble(out var seconds) && seconds > 0)
            {
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            }

            var refreshed = new ClaudeCredentials(
                accessToken,
                expiresAt,
                ReadString(root, "subscription_type", "subscriptionType") ?? current.SubscriptionType,
                ReadString(root, "refresh_token", "refreshToken") ?? current.RefreshToken);

            LastRefreshCacheWritten = _saveRefreshedCredentials(refreshed);
            if (!LastRefreshCacheWritten)
            {
                LastRefreshError = "Kalan önbelleğine yazılamadı";
            }

            return refreshed;
        }
        catch (JsonException)
        {
            LastRefreshError = "Yanıt JSON olarak ayrıştırılamadı";
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LastRefreshError = $"Ağ hatası: {ex.GetType().Name}";
            return null;
        }
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
        LastRefreshAttempted = false;
        LastRefreshStatusCode = null;
        LastRefreshCacheWritten = false;
        LastRefreshError = null;
    }

    private void SetCredentialDiagnostics(ClaudeCredentials? credentials)
    {
        LastCredentialsAvailable = credentials is not null;
        LastCredentialsExpired = credentials?.IsExpired == true;
        LastCredentialsHasRefreshToken = !string.IsNullOrWhiteSpace(credentials?.RefreshToken);
        LastCredentialsExpiresAt = credentials?.ExpiresAt;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        return null;
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
        ("five_hour",        WindowKind.Session, "5 saatlik",       TimeSpan.FromHours(5)),
        ("seven_day",        WindowKind.Weekly,  "Haftalık",        TimeSpan.FromDays(7)),
        ("seven_day_opus",   WindowKind.Weekly,  "Haftalık · Opus", TimeSpan.FromDays(7)),
        ("seven_day_sonnet", WindowKind.Weekly,  "Haftalık · Sonnet", TimeSpan.FromDays(7)),
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
