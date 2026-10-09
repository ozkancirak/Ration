using System.Runtime.InteropServices;
using Ration.Core.Layout;

namespace Ration.Platform.Windows.Interop;

/// <summary>
/// Açılır pencerenin dayandığı nokta: simge dikdörtgeni, bulunduğu ekran ve o ekranın DPI'ı.
/// Pencere boyutu hedef ekranın DPI'ıyla hesaplanmalıdır; pencerenin o an durduğu ekran
/// başka bir ölçekte olabilir.
/// </summary>
public readonly record struct FlyoutAnchor(Bounds Icon, Bounds Monitor, Bounds Work, uint Dpi)
{
    public FlyoutPlacement Place(int width, int height) =>
        FlyoutGeometry.Place(Icon, Monitor, Work, width, height, FlyoutGeometry.ScaledMargin(Dpi));
}

public static class FlyoutPositioner
{
    /// <summary>
    /// Simge dikdörtgenini bulur: önce kabuk, olmazsa fare imleci. Ekran bilgisi alınamazsa null.
    /// </summary>
    public static FlyoutAnchor? ResolveAnchor(Guid trayIconGuid, IntPtr hWnd, uint uID)
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

        if (hresult != 0 || iconRect.Width <= 0 || iconRect.Height <= 0)
        {
            if (!NativeMethods.GetCursorPos(out var pt)) return null;
            iconRect = new NativeMethods.RECT
            {
                Left = pt.X - 10,
                Right = pt.X + 10,
                Top = pt.Y - 10,
                Bottom = pt.Y + 10
            };
        }

        return ForRect(iconRect);
    }

    /// <summary>Verilen dikdörtgenin bulunduğu ekran ve DPI'ıyla dayanak.</summary>
    public static FlyoutAnchor? ForRect(NativeMethods.RECT rect)
    {
        IntPtr hMonitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info)) return null;

        return new FlyoutAnchor(
            ToBounds(rect),
            ToBounds(info.rcMonitor),
            ToBounds(info.rcWork),
            DpiOfMonitor(hMonitor));
    }

    /// <summary>Ekranın etkin DPI'ı; sorgu başarısızsa sistem DPI'ı.</summary>
    public static uint DpiOfMonitor(IntPtr hMonitor)
    {
        if (hMonitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 &&
            dpiX != 0)
        {
            return dpiX;
        }

        var system = NativeMethods.GetDpiForSystem();
        return system == 0 ? 96 : system;
    }

    private static Bounds ToBounds(NativeMethods.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
