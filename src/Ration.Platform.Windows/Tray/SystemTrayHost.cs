using System.Runtime.InteropServices;
using Ration.Core.Diagnostics;
using Ration.Platform.Windows.Interop;

namespace Ration.Platform.Windows.Tray;

/// <summary>
/// Raw Shell_NotifyIcon tray icon. Replaces WinForms NotifyIcon; to avoid bringing all of WinForms
/// into the app, we create the hidden window ourselves.
///
/// Ownership: the HICON belongs to the caller. <see cref="UpdateIcon"/> gives the new handle to the shell;
/// only AFTER that does the caller destroy the previous handle.
/// It must be created on a thread with a message loop (the WinUI UI thread).
/// </summary>
public sealed class SystemTrayHost : IDisposable
{
    private const uint IconId = 1;

    private readonly WndProc _wndProc; // Keep from being garbage collected: the native side calls this delegate
    private readonly string _className = "Ration.Tray." + Guid.NewGuid().ToString("N");
    private readonly uint _taskbarCreated;
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private string _tooltip = "Ration";
    private bool _added;
    private bool _disposed;

    public event Action? LeftClicked;
    public event Action? RightClicked;

    public SystemTrayHost()
    {
        _wndProc = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);

        // A message-only window (HWND_MESSAGE) does not receive the TaskbarCreated broadcast; so a top-level
        // window that is never shown is created (WinForms NotifyIcon does the same).
        _hwnd = CreateWindowEx(0, _className, "Ration Tray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Trace.Error("tray", $"create-window-failed error={Marshal.GetLastWin32Error()}");
        }
    }

    public void UpdateIcon(IntPtr hIcon)
    {
        if (_disposed || hIcon == IntPtr.Zero) return;

        _icon = hIcon;
        Apply();
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;

        // szTip is 128 characters (the last one null); supported Windows versions show all of it.
        _tooltip = tooltip.Length > 127 ? tooltip[..124] + "…" : tooltip;
        if (_added) Apply();
    }

    /// <summary>The icon's screen rectangle; false if the shell has not added it yet.</summary>
    public bool TryGetBounds(out NativeMethods.RECT bounds)
    {
        bounds = default;
        if (_disposed || !_added) return false;

        var identifier = new NativeMethods.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = IconId,
        };
        return NativeMethods.Shell_NotifyIconGetRect(ref identifier, out bounds) == 0 && bounds.Width > 0;
    }

    private void Apply()
    {
        if (_hwnd == IntPtr.Zero || _icon == IntPtr.Zero) return;

        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = _icon,
            szTip = _tooltip,
        };

        if (_added && NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data)) return;

        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (!_added) Trace.Error("tray", "shell-notify-add-failed");
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_TRAYICON)
        {
            switch ((uint)lParam.ToInt64() & 0xFFFF)
            {
                case NativeMethods.WM_LBUTTONUP:
                    Trace.Info("tray", "left-click");
                    LeftClicked?.Invoke();
                    break;
                case NativeMethods.WM_RBUTTONUP:
                    Trace.Info("tray", "right-click");
                    RightClicked?.Invoke();
                    break;
            }
            return IntPtr.Zero;
        }

        // Explorer restarted: the icon disappeared, add it again.
        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            _added = false;
            Apply();
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = IconId,
            };
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        }

        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        UnregisterClass(_className, GetModuleHandle(null));
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
