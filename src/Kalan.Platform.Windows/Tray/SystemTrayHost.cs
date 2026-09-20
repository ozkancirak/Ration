using System.Drawing;
using System.Windows.Forms;
using Kalan.Platform.Windows.Interop;

namespace Kalan.Platform.Windows.Tray;

public sealed class SystemTrayHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private Icon? _currentIcon;
    private bool _disposed;

    public event Action? LeftClicked;
    public event Action? SettingsClicked;
    public event Action? ExitClicked;

    public SystemTrayHost()
    {
        _contextMenu = new ContextMenuStrip();

        var toggleItem = new ToolStripMenuItem("Göster / Gizle")
        {
            Font = new Font(_contextMenu.Font, FontStyle.Bold)
        };
        toggleItem.Click += (s, e) => LeftClicked?.Invoke();

        var settingsItem = new ToolStripMenuItem("Ayarlar…");
        settingsItem.Click += (s, e) => SettingsClicked?.Invoke();

        var exitItem = new ToolStripMenuItem("Çıkış");
        exitItem.Click += (s, e) => ExitClicked?.Invoke();

        _contextMenu.Items.Add(toggleItem);
        _contextMenu.Items.Add(settingsItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Visible = false,
            Text = "Kalan"
        };

        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                LeftClicked?.Invoke();
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
        _contextMenu.Dispose();

        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
