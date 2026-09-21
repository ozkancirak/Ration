using System;
using Microsoft.UI.Xaml;
using Kalan.Core.Diagnostics;
using Kalan.App.Views;
using Kalan.Platform.Windows.App;

namespace Kalan.App;

public partial class App : Application
{
    private FlyoutWindow? _flyoutWindow;
    private SettingsWindow? _settingsWindow;
    private SingleInstanceLease? _singleInstance;

    public App()
    {
        InitializeComponent();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var type = e.ExceptionObject?.GetType().Name ?? "unknown";
            Trace.Error("app.unhandled", $"type={type}");
        };

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            Trace.Info("app", "process-exit");
            _singleInstance?.Dispose();
        };

        this.UnhandledException += (s, e) =>
        {
            Trace.Error("xaml", $"type={e.Exception.GetType().Name}");
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        string? screenshotPath = null;
        string? verificationProvider = GetOption(cmdArgs, "--provider");
        for (int i = 0; i < cmdArgs.Length; i++)
        {
            if (cmdArgs[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < cmdArgs.Length && !cmdArgs[i + 1].StartsWith("--"))
                {
                    screenshotPath = cmdArgs[i + 1];
                }
                else
                {
                    screenshotPath = "screenshot.png";
                }
                break;
            }
        }

        try
        {
            Trace.Info("app", "launch");
            if (!SingleInstanceLease.TryAcquire(cmdArgs, out _singleInstance))
            {
                Environment.Exit(0);
                return;
            }

            if (HasFlag(cmdArgs, "--log"))
            {
                foreach (var line in Trace.ReadLastLines(100))
                {
                    Console.WriteLine(line);
                }

                Environment.Exit(0);
                return;
            }

            // --show/--settings/--menu/--selftest/--screenshot: el ile düzen ve
            // görsel doğrulama kancaları. Self-test normal açılışta çalışmaz.
            Window? targetWindow = null;
            if (HasFlag(cmdArgs, "--selftest"))
            {
                _flyoutWindow = new FlyoutWindow();
                _flyoutWindow.InitializeHidden();
                _flyoutWindow.ShowMenuForVerification();
                Trace.Info("selftest", "launch");
                _ = SelfTestRunner.RunAsync(_flyoutWindow, GetOption(cmdArgs, "--screenshot-dir"));
            }
            else if (HasFlag(cmdArgs, "--settings"))
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.ShowAndFocus();
                targetWindow = _settingsWindow;
                Trace.Info("window", "settings.show command-line");
            }
            else if (cmdArgs.Any(a => a.Equals("--menu", StringComparison.OrdinalIgnoreCase)))
            {
                _flyoutWindow = new FlyoutWindow();
                _flyoutWindow.InitializeHidden();
                _flyoutWindow.ShowMenuForVerification();
                targetWindow = _flyoutWindow.MenuWindow;
                Trace.Info("menu", "show command-line");
            }
            else if (cmdArgs.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)) || !string.IsNullOrEmpty(screenshotPath))
            {
                _flyoutWindow = new FlyoutWindow();
                if (verificationProvider is not null &&
                    !_flyoutWindow.SelectProviderForVerification(verificationProvider))
                {
                    Trace.Error("verification", $"unknown-provider provider={verificationProvider}");
                    Environment.Exit(1);
                    return;
                }
                _flyoutWindow.ShowFlyout();
                targetWindow = _flyoutWindow;
                Trace.Info("window", "flyout.show command-line");
            }
            else
            {
                _flyoutWindow = new FlyoutWindow();
                _flyoutWindow.InitializeHidden();
                Trace.Info("window", "flyout.hidden tray-ready");
            }

            if (!string.IsNullOrEmpty(screenshotPath) && targetWindow != null)
            {
                IntPtr targetHwnd = WinRT.Interop.WindowNative.GetWindowHandle(targetWindow);
                _ = Task.Run(async () =>
                {
                    // Arayüz bileşenlerinin tam çizilmesi ve animasyonun oturması için bekleme
                    await Task.Delay(verificationProvider is null ? 800 : 6000);
                    bool ok = await WindowScreenshotHelper.CaptureWindowAsync(targetHwnd, screenshotPath, delayMs: 0);
                    Trace.Info("screenshot", ok ? "captured" : "failed");
                    Environment.Exit(ok ? 0 : 1);
                });
            }
        }
        catch (Exception ex)
        {
            Trace.Error("app.launch", $"type={ex.GetType().Name}");
            if (!string.IsNullOrEmpty(screenshotPath))
            {
                Environment.Exit(1);
            }
        }
    }

    private static bool HasFlag(string[] argv, string flag) =>
        argv.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] argv, string name)
    {
        for (int i = 0; i < argv.Length - 1; i++)
        {
            if (!string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = argv[i + 1];
            return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
        }

        return null;
    }
}
