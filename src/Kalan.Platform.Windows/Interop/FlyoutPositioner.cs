using System.Runtime.InteropServices;

namespace Kalan.Platform.Windows.Interop;

public enum FlyoutEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

public static class FlyoutPositioner
{
    private const int Margin = 8;

    public static (int X, int Y) CalculatePosition(Guid trayIconGuid, int windowWidth, int windowHeight)
        => CalculatePosition(trayIconGuid, IntPtr.Zero, 0, windowWidth, windowHeight);

    public static (int X, int Y) CalculatePosition(Guid trayIconGuid, IntPtr hWnd, uint uID, int windowWidth, int windowHeight)
        => CalculatePosition(trayIconGuid, hWnd, uID, windowWidth, windowHeight, out _);

    public static (int X, int Y) CalculatePosition(
        Guid trayIconGuid,
        IntPtr hWnd,
        uint uID,
        int windowWidth,
        int windowHeight,
        out FlyoutEdge edge)
    {
        edge = FlyoutEdge.Bottom;
        var identifier = new NativeMethods.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONIDENTIFIER)),
            hWnd = hWnd,
            uID = uID,
            guidItem = trayIconGuid
        };

        NativeMethods.RECT iconRect = default;
        int hresult = -1;

        if (trayIconGuid != Guid.Empty || hWnd != IntPtr.Zero)
        {
            hresult = NativeMethods.Shell_NotifyIconGetRect(ref identifier, out iconRect);
        }

        // If Shell_NotifyIconGetRect fails, fallback to mouse cursor pos
        if (hresult != 0 || iconRect.Width <= 0 || iconRect.Height <= 0)
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                iconRect = new NativeMethods.RECT
                {
                    Left = pt.X - 10,
                    Right = pt.X + 10,
                    Top = pt.Y - 10,
                    Bottom = pt.Y + 10
                };
            }
            else
            {
                return (100, 100);
            }
        }

        IntPtr hMonitor = NativeMethods.MonitorFromRect(ref iconRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO))
        };

        if (!NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo))
        {
            return (iconRect.Left, iconRect.Top - windowHeight - Margin);
        }

        var work = monitorInfo.rcWork;
        var full = monitorInfo.rcMonitor;

        int targetX = iconRect.Left + (iconRect.Width / 2) - (windowWidth / 2);
        int targetY;

        // Detect taskbar location by comparing work area with full monitor area
        bool taskbarAtBottom = work.Bottom < full.Bottom;
        bool taskbarAtTop = work.Top > full.Top;
        bool taskbarAtLeft = work.Left > full.Left;
        bool taskbarAtRight = work.Right < full.Right;

        if (taskbarAtTop)
        {
            edge = FlyoutEdge.Top;
            // Taskbar is at the top: place window below icon
            targetY = iconRect.Bottom + Margin;
        }
        else if (taskbarAtLeft)
        {
            edge = FlyoutEdge.Left;
            // Taskbar is on the left: place window to the right of icon
            targetX = iconRect.Right + Margin;
            targetY = iconRect.Top + (iconRect.Height / 2) - (windowHeight / 2);
        }
        else if (taskbarAtRight)
        {
            edge = FlyoutEdge.Right;
            // Taskbar is on the right: place window to the left of icon
            targetX = iconRect.Left - windowWidth - Margin;
            targetY = iconRect.Top + (iconRect.Height / 2) - (windowHeight / 2);
        }
        else
        {
            // Default (Bottom taskbar): place window above icon
            targetY = iconRect.Top - windowHeight - Margin;
        }

        // Clamp to work area bounds
        if (targetX < work.Left + Margin)
        {
            targetX = work.Left + Margin;
        }
        else if (targetX + windowWidth > work.Right - Margin)
        {
            targetX = work.Right - Margin - windowWidth;
        }

        if (targetY < work.Top + Margin)
        {
            targetY = work.Top + Margin;
        }
        else if (targetY + windowHeight > work.Bottom - Margin)
        {
            targetY = work.Bottom - Margin - windowHeight;
        }

        return (targetX, targetY);
    }
}
