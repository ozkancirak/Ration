using Kalan.Core.Abstractions;
using Kalan.Core.Providers.Antigravity;
using Kalan.Core.Providers.Claude;
using Kalan.Core.Providers.Codex;

namespace Kalan.Core.Providers;

/// <summary>
/// Uygulamanın sağlayıcı bileşimini tek yerde tutar. UI sağlayıcıların
/// hangilerinin var olduğunu bilmez; yalnızca bu kaydın çıktısını tüketir.
/// </summary>
public static class ProviderRegistry
{
    public static IReadOnlyList<IUsageProvider> CreateAll(HttpClient http) =>
    [
        new ClaudeProvider(http),
        new CodexProvider(http),
        new AntigravityProvider(http),
    ];
}
