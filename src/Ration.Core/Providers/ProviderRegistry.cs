using Ration.Core.Abstractions;
using Ration.Core.Providers.Antigravity;
using Ration.Core.Providers.Claude;
using Ration.Core.Providers.Codex;
using Ration.Core.Providers.OpenCode;

namespace Ration.Core.Providers;

/// <summary>
/// Uygulamanın sağlayıcı bileşimini tek yerde tutar. UI sağlayıcıların
/// hangilerinin var olduğunu bilmez; yalnızca bu kaydın çıktısını tüketir.
/// </summary>
public static class ProviderRegistry
{
    public static IReadOnlyList<IUsageProvider> CreateAll(
        HttpClient http,
        Func<IReadOnlyList<int>>? findAntigravityProcessPorts = null,
        Func<IReadOnlyList<AntigravityProcessEndpoint>>? findAntigravityProcessEndpoints = null) =>
    [
        new ClaudeProvider(http),
        new CodexProvider(http),
        new AntigravityProvider(http, findAntigravityProcessPorts, findAntigravityProcessEndpoints),
        new OpenCodeProvider(http),
    ];
}
