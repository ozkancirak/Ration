using System.Net;
using System.Net.Http.Headers;
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

    private readonly HttpClient _http;
    private readonly Func<ClaudeCredentials?> _credentials;

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

    public ClaudeOAuthUsageSource(
        HttpClient http,
        Func<ClaudeCredentials?>? credentials = null)
    {
        _http = http;
        _credentials = credentials ?? (() => ClaudeCredentialStore.TryRead());
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
                "~/.claude/.credentials.json bulunamadı ya da claudeAiOauth içermiyor.", Kind);
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
                var reason = response.StatusCode == HttpStatusCode.Unauthorized && credentials.IsExpired
                    ? "Oturum yenilenmeli — Claude Code'u bir kez çalıştır"
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
