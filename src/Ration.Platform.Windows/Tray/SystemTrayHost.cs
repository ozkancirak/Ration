using System.Drawing;
using System.Windows.Forms;
using Ration.Core.Diagnostics;
using Ration.Platform.Windows.Interop;

namespace Ration.Platform.Windows.Tray;

public sealed class SystemTrayHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private Icon? _currentIcon;
    private bool _disposed;

    public event Action? LeftClicked;
    public event Action? RightClicked;

    public SystemTrayHost()
    {
        // WinForms menüsü yok: sağ tık native TrayMenuWindow'u açar (bkz. FlyoutWindow).
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = null,
            Visible = false,
            Text = "Ration"
        };

        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Trace.Info("tray", "left-click");
                LeftClicked?.Invoke();
            }
        };

        _notifyIcon.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                Trace.Info("tray", "right-click");
                RightClicked?.Invoke();
            }
        };
    }

    public void UpdateIcon(IntPtr hIcon)
    {
        if (_disposed || hIcon == IntPtr.Zero) return;

        Icon? previousIcon = _currentIcon;

        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            _currentIcon = (Icon)tempIcon.Clone();
            _notifyIcon.Icon = _currentIcon;
            _notifyIcon.Visible = true;
        }
        catch { }

        previousIcon?.Dispose();
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        // NotifyIcon text is limited to 63 chars on older shells, clamp to 63 safe
        if (tooltip.Length > 63)
        {
            tooltip = tooltip.Substring(0, 60) + "…";
        }
        _notifyIcon.Text = tooltip;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
