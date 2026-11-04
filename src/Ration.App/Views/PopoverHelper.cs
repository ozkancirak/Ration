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
using Ration.Core.Layout;
using Ration.Platform.Windows.Interop;

namespace Ration.App.Views;

/// <summary>
/// Common shell of borderless popup windows (flyout, tray menu).
/// The behavior comes from a single source; no copy-paste across windows:
/// title-less + always-on-top presenter, taskbar/Alt+Tab invisibility,
/// acrylic + rounded corners, light dismiss (focus loss/Esc/close) and bringing to front.
/// Positioning is already done with the shared <see cref="FlyoutPositioner"/>.
/// </summary>
internal static class PopoverHelper
{
    /// <summary>Win32 handle + AppWindow; every popup window setup starts with this.</summary>
    public static (AppWindow AppWindow, IntPtr Hwnd) Attach(Window window)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return (AppWindow.GetFromWindowId(windowId), hwnd);
    }

    /// <summary>
    /// Visual shell: acrylic, title-less always-on-top presenter,
    /// taskbar/Alt+Tab invisibility (<see cref="AppWindow.IsShownInSwitchers"/>),
    /// rounded corners.
    /// </summary>
    public static void ConfigureChrome(Window window, AppWindow appWindow, IntPtr hwnd)
    {
        try
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            // Acrylic fallback: the window still opens.
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

        // SetBorderAndTitleBar(false, false) leaves WS_DLGFRAME: a 3px non-client frame remains on every edge
        // and Windows paints it with a flat light color instead of acrylic (a white frame visible in both
        // themes). The frame bits are removed and recalculated; the client area becomes the whole window.
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

        // DWM draws a 1px frame in the system color on every window; on a dark panel it stayed as a white line,
        // on a light panel as a low-contrast one. It is turned off; instead the themed border that WinUI
        // flyouts use is drawn at the XAML root (SurfaceStrokeColorFlyoutBrush, OverlayCornerRadius).
        RemoveDwmBorder(hwnd);
    }

    private static void RemoveDwmBorder(IntPtr hwnd)
    {
        int noBorder = NativeMethods.DWMWA_COLOR_NONE;
        int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        if (hr != 0) Trace.Error("window", $"dwm-border hr=0x{hr:X8}");
    }

    /// <summary>
    /// Light dismiss: close request + focus loss + Esc → <paramref name="hide"/>.
    /// <paramref name="onDeactivated"/>: extra work on focus loss (e.g. the timestamp that keeps the same click
    /// from closing the window and immediately reopening it).
    /// <paramref name="canDismissOnDeactivate"/>: if it returns false, focus loss does not close the window
    /// (pinned flyout); Esc and the close request still close it.
    /// </summary>
    public static void ConfigureDismissal(
        Window window,
        AppWindow appWindow,
        Action hide,
        FrameworkElement escRoot,
        Action? onDeactivated = null,
        Func<bool>? canDismissOnDeactivate = null)
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
                if (canDismissOnDeactivate?.Invoke() ?? true) hide();
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

    /// <summary>Show, activate, bring to front. Because the tray click gives us foreground rights,
    /// SetForegroundWindow succeeds here (focus + Esc work).</summary>
    public static void ShowPopover(
        AppWindow appWindow,
        Window window,
        IntPtr hwnd,
        FrameworkElement root,
        FlyoutEdge edge)
    {
        appWindow.Show();
        window.Activate();
        RemoveDwmBorder(hwnd); // re-applied on show; WinUI can redraw the frame on activation
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
    /// The window size changes natively in one step; the content settles into its new height
    /// within 150 ms via Composition Scale.Y at the same time. So the tab switch does not look like
    /// a jump and the UI thread is not kept busy with a timer.
    /// </summary>
    public static void ResizeWithAnimation(
        AppWindow appWindow,
        FrameworkElement root,
        Windows.Graphics.RectInt32 bounds,
        int currentHeight,
        bool anchorBottom)
    {
        var targetHeight = bounds.Height;

        // The position is given together: when the taskbar is at the bottom the bottom edge must stay fixed,
        // whereas ResizeClient alone fixed the top edge and moved the bottom edge
        // (a gap to the taskbar when shortened, overflow off the screen when lengthened).
        if (currentHeight <= 0 || currentHeight == targetHeight)
        {
            appWindow.MoveAndResize(bounds);
            return;
        }

        var animationsEnabled = AnimationsAllowed();

        var visual = ElementCompositionPreview.GetElementVisual(root);
        visual.StopAnimation("Scale.Y");

        if (!animationsEnabled)
        {
            visual.Scale = new Vector3(1.0f, 1.0f, 1.0f);
            appWindow.MoveAndResize(bounds);
            return;
        }

        appWindow.MoveAndResize(bounds);

        // Scale from the fixed edge: in a bottom-anchored window the content grows from the bottom.
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

    /// <summary>False when the user turned animations off in Windows (Settings > Accessibility > Visual effects).</summary>
    private static bool AnimationsAllowed()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// A short fade for content that was just swapped (a provider tab switch), so the change does not
    /// read as a flash. Skipped when animations are off.
    /// </summary>
    public static void FadeIn(FrameworkElement content)
    {
        var visual = ElementCompositionPreview.GetElementVisual(content);
        visual.StopAnimation("Opacity");

        if (!AnimationsAllowed())
        {
            visual.Opacity = 1.0f;
            return;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f),
            new Vector2(0.2f, 1.0f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(140);
        fade.InsertKeyFrame(0.0f, 0.25f);
        fade.InsertKeyFrame(1.0f, 1.0f, easing);
        visual.StartAnimation("Opacity", fade);
    }

    private static void AnimateEntrance(FrameworkElement root, FlyoutEdge edge)
    {
        var visual = ElementCompositionPreview.GetElementVisual(root);
        var compositor = visual.Compositor;

        if (!AnimationsAllowed())
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
    /// The ceiling for sizing to content: 70% of the work area of the screen the cursor
    /// is on. If it is exceeded the window does not grow and the scroller kicks in.
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
