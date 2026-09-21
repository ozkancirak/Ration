using Kalan.Core.Abstractions;

namespace Kalan.Core.Providers.Antigravity;

public sealed class AntigravityProvider : IUsageProvider
{
    public AntigravityProvider(HttpClient http)
    {
        Sources = [new AntigravityLoopbackUsageSource(http)];
    }

    public string Id => "antigravity";

    public string DisplayName => "Antigravity";

    public ProviderCapabilities Capabilities => new(
        SupportsSessionWindow: true,
        SupportsWeeklyWindow: true,
        SupportsMonthlyWindow: false,
        SupportsCredits: false,
        SupportsCostReport: false);

    public IReadOnlyList<IUsageSource> Sources { get; }
}
