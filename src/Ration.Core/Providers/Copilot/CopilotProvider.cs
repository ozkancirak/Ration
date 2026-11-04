using Ration.Core.Abstractions;
using Ration.Core.Model;

namespace Ration.Core.Providers.Copilot;

/// <summary>
/// GitHub Copilot CLI, read from its local session logs (EXPERIMENTAL). There is no quota to read here,
/// only token usage; the app computes it from the logs when the tab is opened.
/// </summary>
public sealed class CopilotProvider : IUsageProvider
{
    public string Id => "copilot";

    public string DisplayName => "Copilot";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsSessionWindow: false,
        SupportsWeeklyWindow: false,
        SupportsMonthlyWindow: false,
        SupportsCredits: false,
        SupportsCostReport: true);

    public IReadOnlyList<IUsageSource> Sources { get; }

    public CopilotProvider(string? sessionStateDirectory = null)
    {
        Sources = new IUsageSource[] { new CopilotLocalSource(sessionStateDirectory) };
    }
}

public sealed class CopilotLocalSource : IUsageSource
{
    private readonly string _sessionStateDirectory;

    public CopilotLocalSource(string? sessionStateDirectory = null)
    {
        _sessionStateDirectory = sessionStateDirectory ?? KnownPaths.CopilotSessionStateDir;
    }

    public SourceKind Kind => SourceKind.LocalFile;

    // Always tried, so that a missing folder is reported as NotInstalled rather than as "no usable source".
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_sessionStateDirectory))
        {
            return Task.FromResult(Snapshot.Empty(
                "copilot",
                ProviderStatus.NotInstalled,
                L.T("GitHub Copilot CLI session logs were not found.", "GitHub Copilot CLI oturum günlükleri bulunamadı."),
                Kind));
        }

        return Task.FromResult(new UsageSnapshot(
            ProviderId: "copilot",
            Windows: Array.Empty<UsageWindow>(),
            Credits: null,
            Cost: null,
            Status: ProviderStatus.Ok,
            ResolvedVia: Kind,
            FetchedAt: DateTimeOffset.UtcNow,
            StaleReason: null,
            PlanName: L.T("Experimental", "Deneysel"),
            StatusDetail: L.T(
                "Token usage from the local Copilot CLI session logs. Copilot's quota is not read.",
                "Yerel Copilot CLI oturum günlüklerinden token kullanımı. Copilot'un kotası okunmaz.")));
    }
}
