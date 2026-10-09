using System.Runtime.InteropServices;
using Ration.Core.Layout;

namespace Ration.Platform.Windows.Interop;

public static class FlyoutPositioner
{
    public static FlyoutPlacement CalculatePosition(
        Guid trayIconGuid,
        IntPtr hWnd,
        uint uID,
        int windowWidth,
        int windowHeight)
    {
        NativeMethods.RECT iconRect = default;
        int hresult = -1;

        if (trayIconGuid != Guid.Empty || hWnd != IntPtr.Zero)
        {
            var identifier = new NativeMethods.NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONIDENTIFIER)),
                hWnd = hWnd,
                uID = uID,
                guidItem = trayIconGuid
            };
            hresult = NativeMethods.Shell_NotifyIconGetRect(ref identifier, out iconRect);
        }

        // Shell_NotifyIconGetRect başarısızsa fare imlecine düş
        if (hresult != 0 || iconRect.Width <= 0 || iconRect.Height <= 0)
        {
            if (!NativeMethods.GetCursorPos(out var pt)) return new FlyoutPlacement(100, 100, FlyoutEdge.Bottom);
            iconRect = new NativeMethods.RECT
            {
                Left = pt.X - 10,
                Right = pt.X + 10,
                Top = pt.Y - 10,
                Bottom = pt.Y + 10
            };
        }

        IntPtr hMonitor = NativeMethods.MonitorFromRect(ref iconRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            return new FlyoutPlacement(iconRect.Left, iconRect.Top - windowHeight - FlyoutGeometry.MarginDip, FlyoutEdge.Bottom);
        }

        uint dpi = hWnd != IntPtr.Zero ? NativeMethods.GetDpiForWindow(hWnd) : 0;
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();

        return FlyoutGeometry.Place(
            ToBounds(iconRect),
            ToBounds(info.rcMonitor),
            ToBounds(info.rcWork),
            windowWidth,
            windowHeight,
            FlyoutGeometry.ScaledMargin(dpi));
    }

    private static Bounds ToBounds(NativeMethods.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
