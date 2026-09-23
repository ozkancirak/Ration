using System.Runtime.InteropServices;
using Ration.Core.Diagnostics;
using Ration.Platform.Windows.Interop;

namespace Ration.Platform.Windows.Tray;

/// <summary>
/// Ham Shell_NotifyIcon tepsi simgesi. WinForms NotifyIcon'un yerini alır; uygulamaya
/// bütün WinForms'u taşımamak için gizli pencere kendimiz açılır.
///
/// Sahiplik: HICON çağıranındır. <see cref="UpdateIcon"/> yeni handle'ı kabuğa verir;
/// çağıran ancak bundan SONRA önceki handle'ı yok eder (AGENTS.md §5, HICON ömrü).
/// Oluşturulduğu thread'de mesaj döngüsü olmalıdır (WinUI UI thread'i).
/// </summary>
public sealed class SystemTrayHost : IDisposable
{
    private const uint IconId = 1;

    private readonly WndProc _wndProc; // GC toplamasın: native taraf bu delegeyi çağırır
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

        // Mesaj-yalnız pencere (HWND_MESSAGE) TaskbarCreated yayınını almaz; bu yüzden
        // hiç gösterilmeyen üst düzey pencere açılır (WinForms NotifyIcon da böyle yapar).
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

        // szTip 128 karakter; eski kabuklar 63'te kesiyor.
        _tooltip = tooltip.Length > 63 ? tooltip[..60] + "…" : tooltip;
        if (_added) Apply();
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

        // Explorer yeniden başladı: simge kayboldu, tekrar ekle.
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
