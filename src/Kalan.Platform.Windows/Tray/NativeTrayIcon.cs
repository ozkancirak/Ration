using System;
using System.Runtime.InteropServices;
using Kalan.Platform.Windows.Interop;

namespace Kalan.Platform.Windows.Tray;

/// <summary>
/// Saf Win32 Shell_NotifyIcon sarmalayıcısı.
/// H.NotifyIcon ve WinForms bağımlılığı olmadan, doğrudan pencere tanıtıcısına (HWND)
/// bağlı olarak çalışır. GUID tabanlı ekleme başarısız olursa otomatik olarak
/// standart (HWND, uID) moduna düşer.
/// </summary>
public sealed class NativeTrayIcon : IDisposable
{
    private readonly IntPtr _hWnd;
    private readonly uint _uID;
    private readonly Guid _guid;
    private NativeMethods.NOTIFYICONDATA _data;
    private bool _isCreated;
    private bool _usesGuid;
    private bool _disposed;

    public bool IsCreated => _isCreated;
    public bool UsesGuid => _usesGuid;
    public uint UId => _uID;

    public NativeTrayIcon(IntPtr hWnd, uint uID = 1, Guid guid = default)
    {
        _hWnd = hWnd;
        _uID = uID;
        _guid = guid;
    }

    public bool Create(IntPtr hIcon, string tooltip)
    {
        if (_disposed || hIcon == IntPtr.Zero) return false;

        string safeTip = string.IsNullOrEmpty(tooltip) ? "Kalan" : tooltip;
        if (safeTip.Length > 127) safeTip = safeTip.Substring(0, 124) + "…";

        _data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = _uID,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = hIcon,
            szTip = safeTip
        };

        int err1 = 0;
        int err2 = 0;
        bool guidSuccess = false;

        if (_guid != Guid.Empty)
        {
            _data.uFlags |= NativeMethods.NIF_GUID;
            _data.guidItem = _guid;
            guidSuccess = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data);
            err1 = Marshal.GetLastWin32Error();
            if (guidSuccess)
            {
                _isCreated = true;
                _usesGuid = true;
                return true;
            }

            // GUID başarısız olursa hemen standart (HWND, uID) ile tekrar dene
            _data.uFlags &= ~NativeMethods.NIF_GUID;
            _data.guidItem = Guid.Empty;
        }

        bool success = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data);
        err2 = Marshal.GetLastWin32Error();
        if (success)
        {
            _isCreated = true;
            _usesGuid = false;
        }
        else
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "startup.log"),
                $"[NativeTray] NIM_ADD failed: cbSize={_data.cbSize}, guidOk={guidSuccess}(err={err1}), stdOk={success}(err={err2}), hWnd={_hWnd}, hIcon={hIcon}\n");
        }

        return success;
    }

    public void UpdateIcon(IntPtr hIcon)
    {
        if (_disposed || !_isCreated || hIcon == IntPtr.Zero) return;

        _data.uFlags = NativeMethods.NIF_ICON;
        _data.hIcon = hIcon;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _data);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed || !_isCreated) return;

        string safeTip = string.IsNullOrEmpty(tooltip) ? "Kalan" : tooltip;
        if (safeTip.Length > 127) safeTip = safeTip.Substring(0, 124) + "…";

        _data.uFlags = NativeMethods.NIF_TIP;
        _data.szTip = safeTip;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isCreated)
        {
            _data.uFlags = 0;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _data);
            _isCreated = false;
        }
    }
}
