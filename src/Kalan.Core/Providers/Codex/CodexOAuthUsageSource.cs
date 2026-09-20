using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kalan.Core.Abstractions;
using Kalan.Core.Model;

namespace Kalan.Core.Providers.Codex;

public sealed class CodexOAuthUsageSource : IUsageSource
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

    private readonly HttpClient _http;
    private readonly Func<CodexCredentials?> _credentials;

    public SourceKind Kind => SourceKind.LocalFile;

    /// <summary>
    /// Tanı amaçlı son ham yanıt. Token içermez ama e-posta ve hesap kimliği içerir;
    /// gösterilmeden önce <see cref="RawResponseRedactor"/> ile maskelenmelidir.
    /// </summary>
    public string? LastRawResponse { get; private set; }

    public CodexOAuthUsageSource(HttpClient http, Func<CodexCredentials?>? credentials = null)
    {
        _http = http;
        _credentials = credentials ?? (() => CodexCredentialStore.TryRead());
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(_credentials() is not null);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        var credentials = _credentials();

        if (credentials is null)
        {
            return Snapshot.Empty("codex", ProviderStatus.AuthRequired,
                "~/.codex/auth.json bulunamadı ya da tokens.access_token içermiyor.", Kind);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", credentials.AccountId);
        }

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LastRawResponse = body;

            if ((int)response.StatusCode == 429)
            {
                return Snapshot.Empty("codex", ProviderStatus.Error,
                    "Hız sınırı (HTTP 429): Çok fazla istek yapıldı, biraz sonra tekrar denenecek.", Kind);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Snapshot.Empty("codex", ProviderStatus.AuthRequired,
                    $"Token reddedildi (HTTP {(int)response.StatusCode}). Codex CLI ile tekrar giriş yapın.", Kind);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Snapshot.Empty("codex", ProviderStatus.Error,
                    $"Beklenmedik yanıt: HTTP {(int)response.StatusCode}", Kind);
            }

            var usage = CodexUsageParser.Parse(body);

            return new UsageSnapshot(
                ProviderId: "codex",
                Windows: usage.Windows,
                Credits: usage.Credits,
                Cost: null,
                Status: usage.Windows.Count > 0 ? ProviderStatus.Ok : ProviderStatus.Degraded,
                ResolvedVia: Kind,
                FetchedAt: DateTimeOffset.UtcNow,
                StaleReason: usage.Windows.Count > 0
                    ? null
                    : "Yanıt alındı ama tanınan pencere yok. 'kalan usage -p codex --raw' ile şemayı kontrol edin.",
                PlanName: usage.PlanName);
        }
        catch (JsonException ex)
        {
            return Snapshot.Empty("codex", ProviderStatus.Error,
                $"JSON ayrıştırılamadı: {ex.Message}", Kind);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Snapshot.Empty("codex", ProviderStatus.Error,
                $"Ağ hatası: {ex.GetType().Name}", Kind);
        }
    }
}

public sealed record CodexUsageData(
    IReadOnlyList<UsageWindow> Windows,
    string? PlanName,
    CreditBalance? Credits);

/// <summary>
/// wham/usage yanıtını ayrıştırır. Doğrulanmış şema (19.09.2026):
///
///   plan_type: "plus"
///   rate_limit.primary_window   { used_percent, limit_window_seconds, reset_after_seconds, reset_at }
///   rate_limit.secondary_window { ... }
///   additional_rate_limits[]    { limit_name, normal_model_slug, rate_limit { primary_window, ... } }
///   credits                     { has_credits, unlimited, balance }
///
/// Pencere adı ve türü <c>limit_window_seconds</c>'tan TÜRETİLİR, sabit yazılmaz:
/// sağlayıcı pencere süresini değiştirirse etiket kendiliğinden doğru kalır.
/// Ayrıştırma yine de toleranslıdır; alan yoksa uydurma veri üretilmez.
/// </summary>
public static class CodexUsageParser
{
    private static readonly string[] PercentNames =
        { "used_percent", "utilization", "percent", "usage_percent" };

    private static readonly string[] ResetAtNames =
        { "reset_at", "resets_at", "resets_at_utc" };

    private static readonly string[] ResetInNames =
        { "reset_after_seconds", "resets_in_seconds", "seconds_until_reset" };

    /// <summary>Geriye dönük kolaylık: yalnızca pencereler.</summary>
    public static IReadOnlyList<UsageWindow> ParseWindows(string json) => Parse(json).Windows;

    public static CodexUsageData Parse(string json)
    {
        var windows = new List<UsageWindow>();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new CodexUsageData(windows, null, null);
        }

        // Ana kotalar. rate_limit sarmalayıcısı yoksa kökte ara.
        var container = root;

        if (root.TryGetProperty("rate_limit", out var rateLimit) &&
            rateLimit.ValueKind == JsonValueKind.Object)
        {
            container = rateLimit;
        }
        else if (root.TryGetProperty("rate_limits", out var rateLimits) &&
                 rateLimits.ValueKind == JsonValueKind.Object)
        {
            container = rateLimits;
        }

        TryAddWindow(container, "primary_window", WindowKind.Session, prefix: null, windows);
        TryAddWindow(container, "secondary_window", WindowKind.Weekly, prefix: null, windows);

        // Model bazlı ek kotalar (ör. "gpt-reserve" tükendiğinde bunu görmek isteriz).
        if (root.TryGetProperty("additional_rate_limits", out var additional) &&
            additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in additional.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                var name = ReadString(entry, "limit_name") ?? ReadString(entry, "normal_model_slug");
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!entry.TryGetProperty("rate_limit", out var entryLimit) ||
                    entryLimit.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                TryAddWindow(entryLimit, "primary_window", WindowKind.Weekly, name, windows);
                TryAddWindow(entryLimit, "secondary_window", WindowKind.Weekly, name, windows);
            }
        }

        return new CodexUsageData(
            windows,
            ReadString(root, "plan_type"),
            ReadCredits(root));
    }

    private static void TryAddWindow(
        JsonElement parent,
        string key,
        WindowKind fallbackKind,
        string? prefix,
        List<UsageWindow> into)
    {
        if (!parent.TryGetProperty(key, out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var percent = JsonHelpers.ReadFirstNumber(element, PercentNames);
        if (percent is null) return;

        var windowSeconds = JsonHelpers.ReadFirstNumber(element, "limit_window_seconds");

        DateTimeOffset? resetsAt = null;

        foreach (var name in ResetAtNames)
        {
            if (!element.TryGetProperty(name, out var resetElement)) continue;

            resetsAt = JsonHelpers.ReadTimestamp(resetElement);
            if (resetsAt is not null) break;
        }

        if (resetsAt is null)
        {
            foreach (var name in ResetInNames)
            {
                if (element.TryGetProperty(name, out var secondsElement) &&
                    secondsElement.ValueKind == JsonValueKind.Number &&
                    secondsElement.TryGetInt64(out var seconds))
                {
                    resetsAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
                    break;
                }
            }
        }

        var label = DescribeWindow(windowSeconds, fallbackKind);
        if (!string.IsNullOrWhiteSpace(prefix)) label = $"{prefix} · {label}";

        // Pencere uzunluğu yanıttan doğrudan gelir (limit_window_seconds).
        TimeSpan? length = windowSeconds is > 0 ? TimeSpan.FromSeconds(windowSeconds.Value) : null;

        var clamped = Math.Clamp(percent.Value, 0, 100);
        into.Add(new UsageWindow(KindFromSeconds(windowSeconds, fallbackKind), clamped, 100, clamped, resetsAt, label, length));
    }

    /// <summary>Pencere uzunluğundan insan okunur ad üretir; süre bilinmiyorsa türe düşer.</summary>
    public static string DescribeWindow(double? windowSeconds, WindowKind fallbackKind)
    {
        if (windowSeconds is not > 0)
        {
            return fallbackKind switch
            {
                WindowKind.Session => "Oturum",
                WindowKind.Daily => "Günlük",
                WindowKind.Weekly => "Haftalık",
                WindowKind.Monthly => "Aylık",
                _ => fallbackKind.ToString(),
            };
        }

        var span = TimeSpan.FromSeconds(windowSeconds.Value);

        if (span.TotalDays >= 1)
        {
            var days = (int)Math.Round(span.TotalDays);
            return days switch
            {
                1 => "Günlük",
                7 => "Haftalık",
                30 or 31 => "Aylık",
                _ => $"{days} günlük",
            };
        }

        var hours = (int)Math.Round(span.TotalHours);
        return hours <= 1 ? "Saatlik" : $"{hours} saatlik";
    }

    public static WindowKind KindFromSeconds(double? windowSeconds, WindowKind fallback)
    {
        if (windowSeconds is not > 0) return fallback;

        var days = TimeSpan.FromSeconds(windowSeconds.Value).TotalDays;

        if (days >= 28) return WindowKind.Monthly;
        if (days >= 6.5) return WindowKind.Weekly;
        if (days >= 0.9) return WindowKind.Daily;

        return WindowKind.Session;
    }

    internal static CreditBalance? ReadCredits(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // balance string ("0") ya da sayı olarak gelebiliyor.
        decimal? balance = null;

        if (credits.TryGetProperty("balance", out var balanceElement))
        {
            if (balanceElement.ValueKind == JsonValueKind.Number &&
                balanceElement.TryGetDecimal(out var numeric))
            {
                balance = numeric;
            }
            else if (balanceElement.ValueKind == JsonValueKind.String &&
                     decimal.TryParse(
                         balanceElement.GetString(),
                         System.Globalization.NumberStyles.Any,
                         System.Globalization.CultureInfo.InvariantCulture,
                         out var parsed))
            {
                balance = parsed;
            }
        }

        if (balance is null) return null;

        return new CreditBalance(balance.Value, null, "credit");
    }

    internal static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
