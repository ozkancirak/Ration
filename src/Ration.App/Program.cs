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
        // Claude Code statusLine köprüsü: durum satırı her yenilendiğinde çağrılır, bu yüzden
        // WinUI/Velopack başlatılmadan en kısa yoldan çalışır ve hemen çıkar.
        if (args.Length > 0 && args[0].Equals("--statusline", StringComparison.OrdinalIgnoreCase))
        {
            RunStatusLine();
            return;
        }

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

    private static void RunStatusLine()
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            var limits = Ration.Core.Providers.Claude.ClaudeStatusLine.Capture(input.ReadToEnd());

            using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            output.Write(limits is null
                ? "Ration"
                : $"Claude · oturum %{100 - limits.FiveHour:F0} kaldı · haftalık %{100 - limits.SevenDay:F0} kaldı");
        }
        catch (Exception ex)
        {
            // Claude Code'un durum satırını asla bozma; yalnızca günlüğe yaz.
            Trace.Error("statusline", $"capture failed type={ex.GetType().Name}");
        }
    }
}
