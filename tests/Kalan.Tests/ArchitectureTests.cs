using Kalan.Core.Abstractions;
using Kalan.Core.Model;
using System.Reflection;

namespace Kalan.Tests;

public class ArchitectureTests
{
    [Fact]
    public void CoreAssembly_MustNotReference_WinUI_Or_WindowsAppSDK()
    {
        // AGENTS.md Rule 2.4: Kalan.Core hiçbir WinUI / WinRT / Microsoft.WindowsAppSDK / System.Windows referansı içeremez.
        var coreAssembly = typeof(IUsageProvider).Assembly;
        var referencedAssemblies = coreAssembly.GetReferencedAssemblies();

        var forbiddenPrefixes = new[]
        {
            "Microsoft.WindowsAppSDK",
            "Microsoft.UI",
            "Microsoft.Graphics",
            "WinRT",
            "System.Windows"
        };

        foreach (var refAsm in referencedAssemblies)
        {
            foreach (var forbidden in forbiddenPrefixes)
            {
                Assert.False(
                    refAsm.Name?.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase) == true,
                    $"Kalan.Core cannot reference '{refAsm.Name}'. Forbidden prefix: '{forbidden}'.");
            }
        }
    }

    [Fact]
    public void UsageSnapshot_CanBeInstantiated_WithValidValues()
    {
        var window = new UsageWindow(
            Kind: WindowKind.Session,
            Used: 62.0,
            Limit: 100.0,
            Percent: 62.0,
            ResetsAt: DateTimeOffset.UtcNow.AddHours(2));

        var snapshot = new UsageSnapshot(
            ProviderId: "claude",
            Windows: new[] { window },
            Credits: null,
            Cost: null,
            Status: ProviderStatus.Ok,
            ResolvedVia: SourceKind.LocalFile,
            FetchedAt: DateTimeOffset.UtcNow,
            StaleReason: null);

        Assert.Equal("claude", snapshot.ProviderId);
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Single(snapshot.Windows);
        Assert.Equal(62.0, snapshot.Windows[0].Percent);
    }
}
