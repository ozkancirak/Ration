using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Kalan.Platform.Windows.Interop;

namespace Kalan.App.Views;

/// <summary>
/// Kenarlıksız açılır pencerelerin (flyout, tepsi menüsü) ortak kabuğu.
/// Davranış tek kaynaktan gelir; pencerelerde kopyala-yapıştır yapılmaz:
/// başlıksız + her zaman üstte sunucu, görev çubuğu/Alt+Tab gizliliği,
/// akrilik + yuvarlak köşe, ışıkla kapanma (odak kaybı/Esc/kapatma) ve öne çıkarma.
/// Konumlandırma zaten ortak <see cref="FlyoutPositioner"/> ile yapılır.
/// </summary>
internal static class PopoverHelper
{
    /// <summary>Win32 tanıtıcı + AppWindow; her açılır pencere kurulumu bununla başlar.</summary>
    public static (AppWindow AppWindow, IntPtr Hwnd) Attach(Window window)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return (AppWindow.GetFromWindowId(windowId), hwnd);
    }

    /// <summary>
    /// Görsel kabuk: akrilik, başlıksız her zaman üstte sunucu,
    /// görev çubuğu/Alt+Tab gizliliği (<see cref="AppWindow.IsShownInSwitchers"/>),
    /// yuvarlak köşe.
    /// </summary>
    public static void ConfigureChrome(Window window, AppWindow appWindow, IntPtr hwnd)
    {
        try
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            // Acrylic fallback: pencere yine de açılır.
        }

        // DWMWA_SYSTEMBACKDROP_TYPE = 38 (3 = Desktop Acrylic)
        int backdropType = 3;
        NativeMethods.DwmSetWindowAttribute(hwnd, 38, ref backdropType, sizeof(int));

        var presenter = (appWindow.Presenter as OverlappedPresenter) ?? OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;

        appWindow.IsShownInSwitchers = false;

        int cornerPreference = (int)NativeMethods.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference,
            sizeof(int));
    }

    /// <summary>
    /// Işıkla kapanma: kapatma isteği + odak kaybı + Esc → <paramref name="hide"/>.
    /// <paramref name="onDeactivated"/>: odak kaybında ek iş (örn. aynı tıklamanın
    /// pencereyi kapatıp hemen yeniden açmasını önleyen zaman damgası).
    /// </summary>
    public static void ConfigureDismissal(
        Window window,
        AppWindow appWindow,
        Action hide,
        FrameworkElement escRoot,
        Action? onDeactivated = null)
    {
        appWindow.Closing += (s, e) =>
        {
            e.Cancel = true;
            hide();
        };

        window.Activated += (s, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                onDeactivated?.Invoke();
                hide();
            }
        };

        escRoot.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                hide();
                e.Handled = true;
            }
        };
    }

    /// <summary>Göster, etkinleştir, öne çıkar. Tray tıklaması bize foreground hakkı
    /// verdiği için SetForegroundWindow burada başarılı olur (odak + Esc çalışır).</summary>
    public static void ShowPopover(AppWindow appWindow, Window window, IntPtr hwnd)
    {
        appWindow.Show();
        window.Activate();
        NativeMethods.SetForegroundWindow(hwnd);
    }
}
