using System.Text.Json;
using Ration.Core.Abstractions;

namespace Ration.Core.Providers.Claude;

public sealed record ClaudeCredentials(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType,
    string? RefreshToken = null)
{
    public bool IsExpired => ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow;
}

/// <summary>
/// ~/.claude/.credentials.json dosyasını SALT OKUNUR açar.
///
/// FileShare.ReadWrite ile açılıp hemen kapatılır: Claude Code aynı anda bu dosyayı
/// yazıyor olabilir, kilit tutmak onun oturumunu bozar. Token süresi dolmuşsa
/// token yine de sunucuya gönderilir; Ration refresh token tüketmez ve kaynak dosyaya
/// ASLA dokunmaz.
/// </summary>
public static class ClaudeCredentialStore
{
    public static ClaudeCredentials? TryRead(string? path = null)
    {
        path ??= KnownPaths.ClaudeCredentialsFile;
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            // Claude Code 2.1.x'te dosyada yalnızca mcpOAuth bulunabiliyor;
            // bu bizim için kullanılabilir bir kimlik değil.
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!oauth.TryGetProperty("accessToken", out var tokenElement)) return null;

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token)) return null;

            DateTimeOffset? expiresAt = null;
            if (oauth.TryGetProperty("expiresAt", out var expiryElement))
            {
                expiresAt = JsonHelpers.ReadTimestamp(expiryElement);
            }

            string? subscription = null;
            if (oauth.TryGetProperty("subscriptionType", out var subElement) &&
                subElement.ValueKind == JsonValueKind.String)
            {
                subscription = subElement.GetString();
            }

            string? refreshToken = null;
            if (oauth.TryGetProperty("refreshToken", out var refreshElement) &&
                refreshElement.ValueKind == JsonValueKind.String)
            {
                refreshToken = refreshElement.GetString();
            }

            return new ClaudeCredentials(token, expiresAt, subscription, refreshToken);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public sealed class ClaudeProvider : IUsageProvider
{
    public string Id => "claude";

    public string DisplayName => "Claude";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsSessionWindow: true,
        SupportsWeeklyWindow: true,
        SupportsMonthlyWindow: false,
        SupportsCredits: false,
        SupportsCostReport: true);

    public IReadOnlyList<IUsageSource> Sources { get; }

    public ClaudeProvider(HttpClient http)
    {
        Sources = new IUsageSource[]
        {
            new ClaudeOAuthUsageSource(http),
            // OAuth 401 (oturum süresi) ya da 429 verdiğinde Claude Code statusLine kaydı.
            new ClaudeStatusLineUsageSource(),
        };
    }
}
