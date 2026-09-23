using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ration.Platform.Windows.Interop;
using RationTrace = Ration.Core.Diagnostics.Trace;

namespace Ration.App.Views;

/// <summary>
/// Tepsi menüsü için geliştirici self-test'i. UIA kullanmaz: düğmeler uygulamanın
/// kendi referanslarından bulunur, konumları XAML dönüşümüyle hesaplanır ve giriş
/// SendInput ile gerçek fare/klavye olayı olarak üretilir.
/// </summary>
internal static class SelfTestRunner
{
    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(45);

    public static async Task RunAsync(FlyoutWindow flyout, string? screenshotDir)
    {
        var failures = new List<string>();
        string outputDirectory;
        try
        {
            outputDirectory = string.IsNullOrWhiteSpace(screenshotDir)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ration",
                    "selftest")
                : Path.GetFullPath(screenshotDir);
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex)
        {
            ReportFailure(failures, $"ekran görüntüsü dizini oluşturulamadı ({ex.GetType().Name})");
            Finish(failures, finalExitRequested: false);
            Environment.Exit(1);
            return;
        }

        var menu = flyout.MenuWindow;
        var buttons = menu.ButtonsForSelfTest;
        var labels = new[] { L.T("Refresh", "Yenile"), L.T("Settings", "Ayarlar"), L.T("Exit", "Çıkış") };

        NativeMethods.POINT originalCursor = default;
        bool cursorCaptured = NativeMethods.GetCursorPos(out originalCursor);
        bool cursorRestored = false;
        bool closeOnExit = false;
        bool finalExitRequested = false;
        bool resultPrinted = false;

        flyout.SelfTestExitRequested = async () =>
        {
            bool afterScreenshot = await CaptureAsync(
                menu,
                Path.Combine(outputDirectory, closeOnExit ? "keyboard-03-cikis-after.png" : "mouse-03-cikis-after.png"),
                failures);
            if (!afterScreenshot)
            {
                Environment.ExitCode = 1;
            }

            finalExitRequested |= closeOnExit;
            RationTrace.Info("selftest", closeOnExit ? "exit-close" : "exit-intercepted");

            if (closeOnExit)
            {
                if (cursorCaptured)
                {
                    NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
                    cursorRestored = true;
                }

                Environment.ExitCode = failures.Count == 0 ? 0 : 1;
                PrintResult(failures, ref resultPrinted);
            }

            return closeOnExit;
        };

        try
        {
            RationTrace.Info("selftest", "start");

            await OpenMenuAsync(menu, failures);
            RationTrace.Info("selftest", "pass=mouse");
            for (int i = 0; i < buttons.Count; i++)
            {
                bool isExit = i == buttons.Count - 1;
                await RunMouseActionAsync(
                    flyout,
                    menu,
                    buttons[i],
                    labels[i],
                    isExit,
                    outputDirectory,
                    failures);
            }

            // Respect the production manual refresh throttle between the two
            // real-input passes; do not fake a successful refresh or bypass it.
            RationTrace.Info("selftest", "waiting manual-refresh cooldown seconds=61");
            await Task.Delay(TimeSpan.FromSeconds(61));
            RationTrace.Info("selftest", "pass=keyboard");
            for (int i = 0; i < buttons.Count; i++)
            {
                bool isExit = i == buttons.Count - 1;
                if (isExit)
                {
                    // Çıkış her iki turda da son eylemdir; fare turunda
                    // gözlemleyip bırakır, klavye turunda gerçek çıkışı verir.
                    closeOnExit = true;
                }

                await RunKeyboardActionAsync(
                    flyout,
                    menu,
                    buttons[i],
                    labels[i],
                    tabCount: i,
                    isExit,
                    outputDirectory,
                    failures);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(failures, $"beklenmeyen hata ({ex.GetType().Name})");
        }
        finally
        {
            flyout.SelfTestExitRequested = null;
            if (cursorCaptured && !cursorRestored)
            {
                NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            }
        }

        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
        PrintResult(failures, ref resultPrinted);

        if (!finalExitRequested)
        {
            RationTrace.Error("selftest", "exit-not-observed");
            Environment.Exit(1);
        }
    }

    private static async Task RunMouseActionAsync(
        FlyoutWindow flyout,
        TrayMenuWindow menu,
        Button button,
        string label,
        bool isExit,
        string outputDirectory,
        List<string> failures)
    {
        if (!await OpenMenuAsync(menu, failures)) return;

        string filePrefix = $"mouse-{Array.IndexOf(menu.ButtonsForSelfTest.ToArray(), button) + 1:00}-{FilePart(label)}";
        await CaptureAsync(menu, Path.Combine(outputDirectory, filePrefix + "-before.png"), failures);

        int clickBefore = CountLog($"[menu] click action={label}");
        int refreshStartBefore = CountLog("[provider.refresh] start");
        int refreshResultBefore = CountLog("[provider.refresh] result");
        int settingsBefore = CountLog("[window] settings.show");
        int exitBefore = CountLog("[selftest] exit-intercepted");

        RationTrace.Info("selftest", $"input=mouse button={label}");
        if (!SendMouseClick(menu.GetButtonScreenRect(button), failures, label)) return;

        bool passed = isExit
            ? await WaitForAsync(() => CountLog("[selftest] exit-intercepted") > exitBefore, ActionTimeout)
            : label == L.T("Refresh", "Yenile")
                ? await WaitForAsync(
                    () => CountLog($"[menu] click action={label}") > clickBefore
                        && CountLog("[provider.refresh] start") > refreshStartBefore
                        && CountLog("[provider.refresh] result") > refreshResultBefore,
                    ActionTimeout)
                : await WaitForAsync(
                    () => CountLog($"[menu] click action={label}") > clickBefore
                        && CountLog("[window] settings.show") > settingsBefore,
                    ActionTimeout);

        if (!passed)
        {
            ReportFailure(failures, $"fare tıklaması doğrulanamadı: {label}");
            return;
        }

        if (isExit)
        {
            // ExitApplication'ın self-test kancası after ekran görüntüsünü alır.
            return;
        }

        Window afterWindow = menu;
        if (label == L.T("Settings", "Ayarlar"))
        {
            afterWindow = flyout.SettingsWindowForSelfTest!;
            await Task.Delay(500);
        }

        await CaptureAsync(afterWindow, Path.Combine(outputDirectory, filePrefix + "-after.png"), failures);
        if (label == L.T("Settings", "Ayarlar"))
        {
            flyout.SettingsWindowForSelfTest?.HideForSelfTest();
        }
    }

    private static async Task RunKeyboardActionAsync(
        FlyoutWindow flyout,
        TrayMenuWindow menu,
        Button button,
        string label,
        int tabCount,
        bool isExit,
        string outputDirectory,
        List<string> failures)
    {
        if (!await OpenMenuAsync(menu, failures)) return;

        string filePrefix = $"keyboard-{Array.IndexOf(menu.ButtonsForSelfTest.ToArray(), button) + 1:00}-{FilePart(label)}";
        await CaptureAsync(menu, Path.Combine(outputDirectory, filePrefix + "-before.png"), failures);

        int clickBefore = CountLog($"[menu] click action={label}");
        int refreshStartBefore = CountLog("[provider.refresh] start");
        int refreshResultBefore = CountLog("[provider.refresh] result");
        int settingsBefore = CountLog("[window] settings.show");
        int exitBefore = CountLog("[selftest] exit-close");

        RationTrace.Info("selftest", $"input=keyboard button={label} tabs={tabCount}");
        for (int i = 0; i < tabCount; i++)
        {
            if (!SendKey(NativeMethods.VK_TAB, failures, label)) return;
            await Task.Delay(80);
        }

        if (!SendKey(NativeMethods.VK_RETURN, failures, label)) return;

        bool passed = isExit
            ? await WaitForAsync(() => CountLog("[selftest] exit-close") > exitBefore, ActionTimeout)
            : label == L.T("Refresh", "Yenile")
                ? await WaitForAsync(
                    () => CountLog($"[menu] click action={label}") > clickBefore
                        && CountLog("[provider.refresh] start") > refreshStartBefore
                        && CountLog("[provider.refresh] result") > refreshResultBefore,
                    ActionTimeout)
                : await WaitForAsync(
                    () => CountLog($"[menu] click action={label}") > clickBefore
                        && CountLog("[window] settings.show") > settingsBefore,
                    ActionTimeout);

        if (!passed)
        {
            ReportFailure(failures, $"klavye girişi doğrulanamadı: {label}");
            return;
        }

        if (isExit)
        {
            // Exit hook kapanış öncesi after ekran görüntüsünü aldı.
            return;
        }

        Window afterWindow = menu;
        if (label == L.T("Settings", "Ayarlar"))
        {
            afterWindow = flyout.SettingsWindowForSelfTest!;
            await Task.Delay(500);
        }

        await CaptureAsync(afterWindow, Path.Combine(outputDirectory, filePrefix + "-after.png"), failures);
        if (label == L.T("Settings", "Ayarlar"))
        {
            flyout.SettingsWindowForSelfTest?.HideForSelfTest();
        }
    }

    private static async Task<bool> OpenMenuAsync(TrayMenuWindow menu, List<string> failures)
    {
        if (menu.IsMenuVisible)
        {
            menu.HideMenu();
            await Task.Delay(80);
        }

        menu.ShowAtCursor();
        bool opened = await WaitForAsync(
            () => menu.IsMenuVisible && menu.ButtonsForSelfTest.All(b => b.ActualWidth > 0 && b.ActualHeight > 0),
            MenuTimeout);
        if (!opened)
        {
            ReportFailure(failures, "menü açılamadı veya düğmeler ölçülemedi");
        }
        else
        {
            // Composition giriş animasyonunun son karesinde dönüşüm ve ekran
            // görüntüsü aynı geometriden okunsun.
            await Task.Delay(250);
        }

        return opened;
    }

    private static async Task<bool> CaptureAsync(Window window, string path, List<string> failures)
    {
        try
        {
            bool ok = await WindowScreenshotHelper.CaptureWindowAsync(window, path, delayMs: 0);
            if (!ok)
            {
                ReportFailure(failures, $"ekran görüntüsü alınamadı: {Path.GetFileName(path)}");
            }

            return ok;
        }
        catch (Exception ex)
        {
            ReportFailure(failures, $"ekran görüntüsü hatası ({ex.GetType().Name})");
            return false;
        }
    }

    private static bool SendMouseClick(NativeMethods.RECT rect, List<string> failures, string label)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            ReportFailure(failures, $"geçersiz ekran dikdörtgeni: {label}");
            return false;
        }

        int x = rect.Left + rect.Width / 2;
        int y = rect.Top + rect.Height / 2;
        int virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int virtualWidth = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        int virtualHeight = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        RationTrace.Info(
            "selftest",
            $"mouse-target button={label} x={x} y={y} virtual={virtualLeft},{virtualTop},{virtualWidth}x{virtualHeight}");

        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_MOUSE,
                Data = new NativeMethods.INPUTUNION
                {
                    MouseInput = new NativeMethods.MOUSEINPUT
                    {
                        Dx = (int)Math.Round((x - virtualLeft) * 65535.0 / Math.Max(1, virtualWidth - 1)),
                        Dy = (int)Math.Round((y - virtualTop) * 65535.0 / Math.Max(1, virtualHeight - 1)),
                        DwFlags = NativeMethods.MOUSEEVENTF_MOVE
                            | NativeMethods.MOUSEEVENTF_ABSOLUTE
                            | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                    },
                },
            },
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_MOUSE,
                Data = new NativeMethods.INPUTUNION
                {
                    MouseInput = new NativeMethods.MOUSEINPUT
                    {
                        DwFlags = NativeMethods.MOUSEEVENTF_LEFTDOWN,
                    },
                },
            },
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_MOUSE,
                Data = new NativeMethods.INPUTUNION
                {
                    MouseInput = new NativeMethods.MOUSEINPUT
                    {
                        DwFlags = NativeMethods.MOUSEEVENTF_LEFTUP,
                    },
                },
            },
        };

        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            ReportFailure(failures, $"fare olayı gönderilemedi: {label} win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        if (NativeMethods.GetCursorPos(out var actualCursor))
        {
            RationTrace.Info("selftest", $"mouse-after button={label} x={actualCursor.X} y={actualCursor.Y}");
        }

        return true;
    }

    private static bool SendKey(ushort virtualKey, List<string> failures, string label)
    {
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                Data = new NativeMethods.INPUTUNION
                {
                    KeyboardInput = new NativeMethods.KEYBDINPUT { VKey = virtualKey },
                },
            },
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                Data = new NativeMethods.INPUTUNION
                {
                    KeyboardInput = new NativeMethods.KEYBDINPUT
                    {
                        VKey = virtualKey,
                        DwFlags = NativeMethods.KEYEVENTF_KEYUP,
                    },
                },
            },
        };

        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            ReportFailure(failures, $"klavye olayı gönderilemedi: {label} win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(80);
        }

        return condition();
    }

    private static int CountLog(string marker) =>
        RationTrace.ReadLastLines(2000).Count(line => line.Contains(marker, StringComparison.Ordinal));

    private static string FilePart(string value) => value switch
    {
        "Yenile" or "Refresh" => "yenile",
        "Ayarlar" or "Settings" => "ayarlar",
        "Çıkış" or "Exit" => "cikis",
        _ => "menu",
    };

    private static void ReportFailure(List<string> failures, string message)
    {
        failures.Add(message);
        RationTrace.Error("selftest", message);
    }

    private static void PrintResult(List<string> failures, ref bool resultPrinted)
    {
        if (resultPrinted) return;
        resultPrinted = true;

        if (failures.Count == 0)
        {
            RationTrace.Info("selftest", "pass");
            Console.WriteLine("SELFTEST PASS");
            return;
        }

        RationTrace.Error("selftest", $"fail count={failures.Count}");
        Console.Error.WriteLine($"SELFTEST FAIL ({failures.Count})");
        foreach (var failure in failures.Distinct())
        {
            Console.Error.WriteLine($"- {failure}");
        }
    }

    private static void Finish(List<string> failures, bool finalExitRequested)
    {
        Environment.ExitCode = failures.Count == 0 && finalExitRequested ? 0 : 1;
        bool printed = false;
        PrintResult(failures, ref printed);
    }
}
