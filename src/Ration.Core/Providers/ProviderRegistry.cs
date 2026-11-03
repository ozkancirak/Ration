using Ration.Core.Abstractions;
using Ration.Core.Providers.Antigravity;
using Ration.Core.Providers.Claude;
using Ration.Core.Providers.Codex;
using Ration.Core.Providers.Cursor;
using Ration.Core.Providers.OpenCode;

namespace Ration.Core.Providers;

/// <summary>
/// Keeps the app's provider composition in one place. The UI does not know which providers
/// exist; it only consumes the output of this registry.
/// </summary>
public static class ProviderRegistry
{
    public static IReadOnlyList<IUsageProvider> CreateAll(
        HttpClient http,
        Func<IReadOnlyList<int>>? findAntigravityProcessPorts = null,
        Func<IReadOnlyList<AntigravityProcessEndpoint>>? findAntigravityProcessEndpoints = null,
        Func<string, string?>? decryptCursorToken = null) =>
    [
        new ClaudeProvider(http),
        new CodexProvider(http),
        new AntigravityProvider(http, findAntigravityProcessPorts, findAntigravityProcessEndpoints),
        new OpenCodeProvider(http),
        new CursorProvider(decryptToken: decryptCursorToken),
    ];
}
