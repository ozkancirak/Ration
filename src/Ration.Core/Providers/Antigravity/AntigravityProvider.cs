using Ration.Core.Abstractions;

namespace Ration.Core.Providers.Antigravity;

public sealed class AntigravityProvider : IUsageProvider
{
    public AntigravityProvider(
        HttpClient http,
        Func<IReadOnlyList<int>>? findProcessPorts = null,
        Func<IReadOnlyList<AntigravityProcessEndpoint>>? findProcessEndpoints = null)
    {
        Sources =
        [
            new AntigravityLoopbackUsageSource(
                http: null,
                findProcessPorts: findProcessPorts,
                findProcessEndpoints: findProcessEndpoints),
        ];
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
