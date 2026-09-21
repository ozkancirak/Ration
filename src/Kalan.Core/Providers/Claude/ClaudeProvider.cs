using System.Text.Json;
using System.Text.Json.Serialization;
using Kalan.Core.Abstractions;

namespace Kalan.Core.Providers.Claude;

public sealed record ClaudeCredentials(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType,
    string? RefreshToken = null)
{
    public bool IsExpired => ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow;
}

/// <summary>
/// ~/.claude/.credentials.json dosyasını SALT OKUNUR açar (AGENTS.md §2.1).
///
/// FileShare.ReadWrite ile açılıp hemen kapatılır: Claude Code aynı anda bu dosyayı
/// yazıyor olabilir, kilit tutmak onun oturumunu bozar. Token süresi dolmuşsa
/// yenileme sonucu Kalan'ın kendi cache'ine yazılır, kaynak dosyaya ASLA dokunulmaz.
/// </summary>
public static class ClaudeCredentialStore
{
    /// <summary>Kalan'ın yenilenmiş Claude OAuth kimliklerini tuttuğu kendi dosyası.</summary>
    public static string CacheFile => Path.Combine(KnownPaths.CacheDir, "claude-oauth.json");

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

    /// <summary>
    /// Kaynak kimliği önceliklidir. Kaynak dosyada süresi geçmiş token varsa,
    /// Kalan'ın kendi cache'i kullanılır; bu, dönen refresh token'ın bir sonraki
    /// yenilemeye taşınmasını da sağlar.
    /// </summary>
    public static ClaudeCredentials? TryReadEffective()
    {
        var source = TryRead();
        var cached = TryRead(CacheFile);

        if (source is null) return cached;
        if (source.IsExpired && cached is not null)
        {
            return cached with { RefreshToken = cached.RefreshToken ?? source.RefreshToken };
        }

        return source;
    }

    /// <summary>
    /// Yenilenmiş kimliği yalnızca Kalan'ın kendi alanına atomik olarak yazar.
    /// Kullanıcının .credentials.json dosyasına hiçbir zaman yazmaz.
    /// </summary>
    public static bool TryWriteCache(ClaudeCredentials credentials)
    {
        var temporary = CacheFile + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(KnownPaths.CacheDir);

            var json = JsonSerializer.Serialize(
                new
                {
                    claudeAiOauth = new
                    {
                        accessToken = credentials.AccessToken,
                        refreshToken = credentials.RefreshToken,
                        expiresAt = credentials.ExpiresAt?.ToUnixTimeMilliseconds(),
                        subscriptionType = credentials.SubscriptionType,
                    }
                },
                new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                });

            File.WriteAllText(temporary, json);
            File.Move(temporary, CacheFile, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
        };
    }
}
