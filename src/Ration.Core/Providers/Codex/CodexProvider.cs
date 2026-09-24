using System.Text.Json;
using Ration.Core.Abstractions;

namespace Ration.Core.Providers.Codex;

public sealed record CodexCredentials(string AccessToken, string? AccountId);

/// <summary>
/// ~/.codex/auth.json dosyasını SALT OKUNUR açar.
/// Bu dosya kullanıcının Codex CLI oturumudur; yazılmaz, kilitlenmez.
/// </summary>
public static class CodexCredentialStore
{
    public static CodexCredentials? TryRead(string? path = null)
    {
        path ??= KnownPaths.CodexAuthFile;
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!tokens.TryGetProperty("access_token", out var tokenElement)) return null;

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token)) return null;

            string? accountId = null;
            if (tokens.TryGetProperty("account_id", out var accountElement) &&
                accountElement.ValueKind == JsonValueKind.String)
            {
                accountId = accountElement.GetString();
            }

            return new CodexCredentials(token, accountId);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public sealed class CodexProvider : IUsageProvider
{
    public string Id => "codex";

    public string DisplayName => "Codex";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsSessionWindow: true,
        SupportsWeeklyWindow: true,
        SupportsMonthlyWindow: false,
        SupportsCredits: true,
        SupportsCostReport: true);

    public IReadOnlyList<IUsageSource> Sources { get; }

    public CodexProvider(HttpClient http)
    {
        Sources = new IUsageSource[]
        {
            new CodexOAuthUsageSource(http),
            // Ağsız ikinci kaynak: yalnızca OAuth başarısız olunca devreye girer.
            new CodexSessionLogUsageSource(),
        };
    }
}
