using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using Ration.Core.Diagnostics;

namespace Ration.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Ration.Core.LegacySettingsMigration.Run();
        Ration.Platform.Windows.App.StartupRegistration.MigrateLegacyEntry();
        try
        {
            // Velopack must run before the WinUI application is initialized. No
            // update is applied here; startup auto-apply is deliberately off.
            VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .Run();
        }
        catch (Exception ex)
        {
            Trace.Error("velopack", $"startup hook type={ex.GetType().Name}");
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
