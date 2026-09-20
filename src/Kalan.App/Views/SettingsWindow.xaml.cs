using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Kalan.Platform.Windows.Interop;
using Kalan.Platform.Windows.Theme;

namespace Kalan.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    public SettingsWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // SystemBackdrop = MicaBackdrop (Kind = Base)
        try
        {
            this.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        catch
        {
            // Mica fallback
        }

        // 5: DWMWA_SYSTEMBACKDROP_TYPE = 38 (2 = Mica)
        int backdropType = 2;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd,
            38 /* DWMWA_SYSTEMBACKDROP_TYPE */,
            ref backdropType,
            sizeof(int));

        // ExtendsContentIntoTitleBar = true, SetTitleBar ile özel başlık
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);

        // 6c: Başlangıç boyutu 580×480 civarı kompakt boyut
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        double scale = dpi / 96.0;

        int width = (int)(580 * scale);
        int height = (int)(480 * scale);
        _appWindow.Resize(new SizeInt32(width, height));

        // Pencere çerçevesi için Immersive Dark Mode
        UpdateWindowFrameTheme();
        WindowsThemeListener.ThemeChanged += OnThemeChanged;

        _appWindow.Closing += (s, e) =>
        {
            // Tamamen kapatmak yerine gizle; tray'den tıklandığında anında açılsın
            e.Cancel = true;
            _appWindow.Hide();
        };
    }

    private void OnThemeChanged(bool isLightTheme)
    {
        this.DispatcherQueue.TryEnqueue(UpdateWindowFrameTheme);
    }

    private void UpdateWindowFrameTheme()
    {
        bool isDark = !WindowsThemeListener.IsAppLightTheme();
        int darkMode = isDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd,
            NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
            ref darkMode,
            sizeof(int));
    }

    public void ShowAndFocus()
    {
        _appWindow.Show();
        this.Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }
}
