using Kalan.Core.Abstractions;
using Kalan.Core.Providers.Antigravity;
using Kalan.Core.Providers.Claude;
using Kalan.Core.Providers.Codex;
using Kalan.Core.Providers.OpenCode;

namespace Kalan.Core.Providers;

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
