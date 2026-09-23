using System.Runtime.InteropServices;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Ration.Core.Diagnostics;
using Ration.Platform.Windows.Interop;

namespace Ration.App.Views;

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

        // SetBorderAndTitleBar(false, false) WS_DLGFRAME'i bırakıyor: her kenarda 3px
        // non-client çerçeve kalır ve Windows onu acrylic yerine düz açık renkle boyar
        // (iki temada da görünen beyaz çerçeve). Çerçeve bitleri kaldırılıp yeniden
        // hesaplatılır; istemci alanı pencerenin tamamı olur.
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_BORDER | NativeMethods.WS_DLGFRAME | NativeMethods.WS_THICKFRAME);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        try
        {
            appWindow.IsShownInSwitchers = false;
        }
        catch
        {
            try
            {
                IntPtr exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
                exStyle = new IntPtr(exStyle.ToInt64() | NativeMethods.WS_EX_TOOLWINDOW);
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
            }
            catch { }
        }

        int cornerPreference = (int)NativeMethods.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference,
            sizeof(int));

        // DWM her pencereye sistem renginde 1px çerçeve çizer; koyu panelde beyaz,
        // açık panelde kontrastsız bir hat olarak kalıyordu. Kapatılır; yerine WinUI
        // flyout'larının kullandığı temalı kenarlık XAML kökünde çizilir
        // (SurfaceStrokeColorFlyoutBrush, OverlayCornerRadius).
        RemoveDwmBorder(hwnd);
    }

    private static void RemoveDwmBorder(IntPtr hwnd)
    {
        int noBorder = NativeMethods.DWMWA_COLOR_NONE;
        int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        if (hr != 0) Trace.Error("window", $"dwm-border hr=0x{hr:X8}");
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
                Trace.Info("window", "popover.deactivated");
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
    public static void ShowPopover(
        AppWindow appWindow,
        Window window,
        IntPtr hwnd,
        FrameworkElement root,
        FlyoutEdge edge)
    {
        appWindow.Show();
        window.Activate();
        RemoveDwmBorder(hwnd); // gösterimde yeniden uygulanır; WinUI etkinleşmede çerçeveyi geri çizebiliyor
        bool foreground = NativeMethods.SetForegroundWindow(hwnd);
        Trace.Info("window", $"popover.show foreground={(foreground ? "ok" : "rejected")}");
        AnimateEntrance(root, edge);
    }

    public static void HidePopover(AppWindow appWindow, FrameworkElement root)
    {
        var visual = ElementCompositionPreview.GetElementVisual(root);
        visual.StopAnimation("Offset.X");
        visual.StopAnimation("Offset.Y");
        visual.StopAnimation("Opacity");
        visual.Offset = Vector3.Zero;
        visual.Opacity = 0.0f;
        appWindow.Hide();
    }

    /// <summary>
    /// Pencere boyutu native olarak tek seferde değişir; içerik aynı anda
    /// Composition Scale.Y ile 150 ms içinde yeni yüksekliğine oturur. Böylece
    /// sekme geçişi bir sıçrama gibi görünmez ve UI thread'i zamanlayıcıyla
    /// meşgul edilmez.
    /// </summary>
    public static void ResizeWithAnimation(
        AppWindow appWindow,
        FrameworkElement root,
        Windows.Graphics.RectInt32 bounds,
        int currentHeight,
        bool anchorBottom)
    {
        var targetHeight = bounds.Height;

        // Konum da birlikte verilir: görev çubuğu alttayken alt kenar sabit kalmalı,
        // yalnızca ResizeClient üst kenarı sabitleyip alt kenarı kaydırıyordu
        // (kısalınca görev çubuğuyla arada boşluk, uzayınca ekrandan taşma).
        if (currentHeight <= 0 || currentHeight == targetHeight)
        {
            appWindow.MoveAndResize(bounds);
            return;
        }

        bool animationsEnabled = true;
        try
        {
            animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch { }

        var visual = ElementCompositionPreview.GetElementVisual(root);
        visual.StopAnimation("Scale.Y");

        if (!animationsEnabled)
        {
            visual.Scale = new Vector3(1.0f, 1.0f, 1.0f);
            appWindow.MoveAndResize(bounds);
            return;
        }

        appWindow.MoveAndResize(bounds);

        // Sabit kenardan ölçekle: alta yaslı pencerede içerik aşağıdan büyür.
        var rasterScale = root.XamlRoot?.RasterizationScale ?? 1.0;
        var pivotY = anchorBottom ? (float)(targetHeight / rasterScale) : 0.0f;
        visual.CenterPoint = new Vector3(0.0f, pivotY, 0.0f);
        var startScale = Math.Clamp((float)currentHeight / targetHeight, 0.1f, 4.0f);
        visual.Scale = new Vector3(1.0f, startScale, 1.0f);

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f),
            new Vector2(0.2f, 1.0f));
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(150);
        animation.InsertKeyFrame(0.0f, startScale);
        animation.InsertKeyFrame(1.0f, 1.0f, easing);
        visual.StartAnimation("Scale.Y", animation);
    }

    private static void AnimateEntrance(FrameworkElement root, FlyoutEdge edge)
    {
        var visual = ElementCompositionPreview.GetElementVisual(root);
        var compositor = visual.Compositor;

        bool animationsEnabled = true;
        try
        {
            animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch { }

        if (!animationsEnabled)
        {
            visual.StopAnimation("Offset.X");
            visual.StopAnimation("Offset.Y");
            visual.StopAnimation("Opacity");
            visual.Offset = Vector3.Zero;
            visual.Opacity = 1.0f;
            return;
        }

        float offset = edge switch
        {
            FlyoutEdge.Bottom => 16.0f,
            FlyoutEdge.Top => -16.0f,
            FlyoutEdge.Left => -16.0f,
            FlyoutEdge.Right => 16.0f,
            _ => 16.0f,
        };
        string axis = edge is FlyoutEdge.Left or FlyoutEdge.Right ? "Offset.X" : "Offset.Y";

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f),
            new Vector2(0.2f, 1.0f));
        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.Duration = TimeSpan.FromMilliseconds(200);
        slide.InsertKeyFrame(0.0f, offset);
        slide.InsertKeyFrame(1.0f, 0.0f, easing);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(200);
        fade.InsertKeyFrame(0.0f, 0.0f);
        fade.InsertKeyFrame(1.0f, 1.0f, easing);

        visual.StartAnimation(axis, slide);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>
    /// İçeriğe göre boyutlandırmanın tavanı: imlecin bulunduğu ekranın
    /// çalışma alanının %70'i. Aşılırsa pencere büyümez, kaydırıcı devreye girer.
    /// </summary>
    public static int WorkAreaMaxHeight()
    {
        var pt = new NativeMethods.POINT();
        if (!NativeMethods.GetCursorPos(out pt))
        {
            pt = new NativeMethods.POINT { X = 100, Y = 100 };
        }
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo)) return 800;
        return Math.Max(200, (int)Math.Round(monitorInfo.rcWork.Height * 0.85));
    }
}
