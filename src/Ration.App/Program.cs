using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using Ration.Core.Diagnostics;

namespace Ration.App;

internal static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static void Main(string[] args)
    {
        // Packaging verification (scripts/package.ps1): does the app start, is the version right.
        // It exits without building a UI and without touching any preference.
        if (args.Length > 0 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
        {
            AttachConsole(AttachParentProcess);
            Console.WriteLine($"Ration {Ration.App.Updates.AppUpdates.CurrentVersion}");
            return;
        }

        // Every preference, language included, is read from SettingsStore; the file and registry values of older
        // versions must be imported before L is first used, otherwise the language is lost on first launch.
        Ration.Core.LegacySettingsMigration.Run();
        Ration.Core.Settings.LegacyPreferenceImport.Run(
            Ration.Core.Settings.SettingsStore.Default,
            Ration.Core.Providers.KnownPaths.CacheDir,
            new Ration.Platform.Windows.App.LegacyRegistryValues());
        Ration.Core.L.ApplyCulture();

        // Claude Code statusLine bridge: called every time the status line refreshes, so it runs by the
        // shortest path without starting WinUI/Velopack and exits immediately.
        if (args.Length > 0 && args[0].Equals("--statusline", StringComparison.OrdinalIgnoreCase))
        {
            RunStatusLine();
            return;
        }

        // It is a windowed app (WinExe), so no console window appears at startup. Modes that write
        // to the terminal (--log, --selftest) attach to the calling terminal's console.
        if (args.Any(a => a.Equals("--log", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(AttachParentProcess);
        }

        Ration.Platform.Windows.App.StartupRegistration.MigrateLegacyEntry();
        try
        {
            // Velopack must run before the WinUI application is initialized. No
            // update is applied here; startup auto-apply is deliberately off.
            VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnBeforeUninstallFastCallback(_ => Ration.Platform.Windows.App.UninstallCleanup.Run())
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
                : Ration.Core.L.T(
                    $"Claude · session {100 - limits.FiveHour:F0}% left · weekly {100 - limits.SevenDay:F0}% left",
                    $"Claude · oturum %{100 - limits.FiveHour:F0} kaldı · haftalık %{100 - limits.SevenDay:F0} kaldı"));
        }
        catch (Exception ex)
        {
            // Never break Claude Code's status line; only write to the log.
            Trace.Error("statusline", $"capture failed type={ex.GetType().Name}");
        }
    }
}
