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
/// Opens ~/.claude/.credentials.json READ-ONLY.
///
/// It is opened with FileShare.ReadWrite and closed right away: Claude Code may be writing this file
/// at the same time, and holding a lock would break its session. If the token has expired, it is still
/// sent to the server; Ration does not consume the refresh token and NEVER touches the source file.
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

            // In Claude Code 2.1.x the file may contain only mcpOAuth;
            // that is not a usable credential for us.
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
            // The Claude Code statusLine record, used when OAuth gives 401 (session expired) or 429.
            new ClaudeStatusLineUsageSource(),
        };
    }
}
