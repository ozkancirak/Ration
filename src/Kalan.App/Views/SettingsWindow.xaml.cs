using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using CommunityToolkit.WinUI.Controls;
using Kalan.Core.Cost;
using Kalan.Core.Providers;
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

        RefreshCostStatus();

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
        RefreshCostStatus();
        _appWindow.Show();
        this.Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    /// <summary>
    /// Maliyet taramasının gerçek durumu: tarama ayarsız her zaman çalışır
    /// (kapatma düğmesi yok, o yüzden pasif toggle yalan söyler); para tutarı
    /// ise pricing.json ister. Durum etiketi + tek satır sebep yazılır.
    /// </summary>
    private void RefreshCostStatus()
    {
        var priced = !PricingTable.LoadOrEmpty().IsEmpty;

        SetCostStatus(ClaudeCostBadge, ClaudeCostStatus, ClaudeCostReason,
            Directory.Exists(KnownPaths.ClaudeProjectsDir), priced);
        SetCostStatus(CodexCostBadge, CodexCostStatus, CodexCostReason,
            Directory.Exists(KnownPaths.CodexSessionsDir), priced);
    }

    private static void SetCostStatus(InfoBadge badge, TextBlock status, TextBlock reason, bool dirExists, bool priced)
    {
        if (!dirExists)
        {
            status.Text = "Kapalı";
            reason.Text = "Oturum log dizini bulunamadı — taranacak veri yok.";
            reason.Visibility = Visibility.Visible;
            // Dikkat rengi nokta; anahtar bu sürümde yoksa mevcut nokta kalır
            // (etiket zaten doğru, çökme yok).
            if (Application.Current.Resources.TryGetValue("AttentionDotInfoBadgeStyle", out var style) && style is Style dot)
            {
                badge.Style = dot;
            }
            return;
        }

        status.Text = "Açık";
        if (priced)
        {
            reason.Visibility = Visibility.Collapsed;
            return;
        }

        reason.Text = "pricing.json bulunamadı — para tutarı yerine yalnızca token gösteriliyor.";
        reason.Visibility = Visibility.Visible;
    }
}
