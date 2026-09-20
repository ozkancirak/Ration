using System.Drawing;
using System.Net.Http;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Kalan.Core.Abstractions;
using Kalan.Core.Model;
using Kalan.Core.Providers.Claude;
using Kalan.Core.Providers.Codex;
using Kalan.Core.Refresh;
using Kalan.Platform.Windows.Interop;
using Kalan.Platform.Windows.Power;
using Kalan.Platform.Windows.Theme;
using Kalan.Platform.Windows.Tray;

namespace Kalan.App.Views;

public sealed partial class FlyoutWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly OverlappedPresenter _presenter;
    private readonly IntPtr _hwnd;
    private readonly SystemTrayHost _tray;
    private readonly HttpClient _http;
    private readonly RefreshScheduler _scheduler;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private DateTimeOffset _lastDeactivatedTime = DateTimeOffset.MinValue;
    private bool _isVisible;
    private double _currentGaugePercent = 0.0;
    private string _currentTooltip = "Kalan";
    private SettingsWindow? _settingsWindow;
    private IntPtr _currentIconHandle = IntPtr.Zero;
    private Icon? _currentIcon;

    public double CurrentClaudePercent => _currentGaugePercent;

    public FlyoutWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // 5: DWM Desktop Acrylic Backdrop ve XAML DesktopAcrylicBackdrop
        try
        {
            this.SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            // Acrylic fallback
        }

        // DWMWA_SYSTEMBACKDROP_TYPE = 38 (3 = Desktop Acrylic)
        int backdropType = 3;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd,
            38 /* DWMWA_SYSTEMBACKDROP_TYPE */,
            ref backdropType,
            sizeof(int));

        // Configure presenter for borderless floating popover
        _presenter = (_appWindow.Presenter as OverlappedPresenter) ?? OverlappedPresenter.Create();
        _presenter.SetBorderAndTitleBar(false, false);
        _presenter.IsResizable = false;
        _presenter.IsAlwaysOnTop = true;
        _presenter.IsMinimizable = false;
        _presenter.IsMaximizable = false;

        // Remove from taskbar and Alt+Tab switchers using native Win32 WS_EX_TOOLWINDOW
        long exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        _appWindow.Closing += (s, e) =>
        {
            e.Cancel = true;
            HideFlyout();
        };

        // Apply rounded corners via DWM
        int cornerPreference = (int)NativeMethods.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference,
            sizeof(int));

        // Immersive Dark Mode
        UpdateWindowFrameTheme();

        // Subclass hook: WM_DWMCOLORIZATIONCOLORCHANGED (0x0320) ve WM_SETTINGCHANGE (0x001A)
        _subclassProc = WindowSubclassProc;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, new UIntPtr(1), IntPtr.Zero);

        // Enable translation for composition entrance animation
        ElementCompositionPreview.SetIsTranslationEnabled(RootLayout, true);

        // Light dismiss on blur / deactivation
        this.Activated += OnWindowActivated;

        // Escape key to dismiss
        RootLayout.KeyDown += OnRootKeyDown;

        // Initialize HTTP Client and RefreshScheduler
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Kalan/0.1");

        var providers = new IUsageProvider[]
        {
            new ClaudeProvider(_http),
            new CodexProvider(_http),
        };

        _scheduler = new RefreshScheduler(providers, options: new RefreshOptions
        {
            Interval = TimeSpan.FromMinutes(5),
            MaxJitter = TimeSpan.FromSeconds(5),
            EmitCachedOnStart = true,
        });

        _scheduler.SnapshotUpdated += OnSnapshotUpdated;

        // Wire window refresh button
        RefreshButton.Click += async (s, e) =>
        {
            RefreshButton.IsEnabled = false;
            try
            {
                await _scheduler.RefreshAllAsync();
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        };

        // Tray: WinForms NotifyIcon (saglam yol). Ikon HICON olarak uretilir,
        // sahiplik SystemTrayHost'a gecer: once yeni ikon kabuga verilir,
        // sonra onceki handle yok edilir (bkz. UpdateTrayIcon).
        _tray = new SystemTrayHost();
        _tray.LeftClicked += () => this.DispatcherQueue.TryEnqueue(Toggle);
        _tray.SettingsClicked += () => this.DispatcherQueue.TryEnqueue(OpenSettingsWindow);
        _tray.ExitClicked += () => this.DispatcherQueue.TryEnqueue(ExitApplication);

        UpdateTrayIcon(_currentGaugePercent, _currentTooltip);

        File.AppendAllText(
            Path.Combine(AppContext.BaseDirectory, "startup.log"),
            "[Tray] Initialized via SystemTrayHost (WinForms NotifyIcon)\n");

        // Theme listeners
        WindowsThemeListener.ThemeChanged += OnTaskbarThemeChanged;
        WindowsThemeListener.AccentChanged += OnAccentChanged;
        WindowsThemeListener.DisplayChanged += OnDisplayChanged;

        _appWindow.Resize(new SizeInt32(1, 1));

        // Start in Efficiency Mode
        EfficiencyModeManager.SetEfficiencyMode(true);

        // Start scheduler loop
        _scheduler.Start();
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint WM_THEMECHANGED = 0x031A;
        const uint WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

        if (uMsg == WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            WindowsThemeListener.NotifyAccentChanged();
        }
        else if (uMsg is WM_THEMECHANGED or WM_SETTINGCHANGE)
        {
            WindowsThemeListener.NotifyAccentChanged();
            WindowsThemeListener.NotifyThemeChanged(WindowsThemeListener.IsTaskbarLightTheme());
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
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

    public void InitializeHidden()
    {
        // WinUI'da ekran disina tasima (Move -32000) E_INVALIDARG atar;
        // dogrusu AppWindow.Hide(). Once _isVisible=false ki Hide'in
        // tetikledigi Deactivated geri girmesin.
        _isVisible = false;
        var visual = ElementCompositionPreview.GetElementVisual(RootLayout);
        visual.Opacity = 0.0f;
        _appWindow.Hide();
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
        }
        _settingsWindow.ShowAndFocus();
    }

    private void ExitApplication()
    {
        WindowsThemeListener.ThemeChanged -= OnTaskbarThemeChanged;
        WindowsThemeListener.AccentChanged -= OnAccentChanged;
        WindowsThemeListener.DisplayChanged -= OnDisplayChanged;

        NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, new UIntPtr(1));

        _tray.Dispose();
        _currentIcon?.Dispose();
        if (_currentIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_currentIconHandle);
            _currentIconHandle = IntPtr.Zero;
        }

        try
        {
            _settingsWindow?.Close();
        }
        catch { }

        try
        {
            _scheduler.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        _http.Dispose();

        Application.Current.Exit();
    }

    private void OnDisplayChanged()
    {
        this.DispatcherQueue.TryEnqueue(() => UpdateTrayIcon(_currentGaugePercent, _currentTooltip));
    }

    private void OnTaskbarThemeChanged(bool isLightTheme)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            UpdateWindowFrameTheme();
            UpdateTrayIcon(_currentGaugePercent, _currentTooltip);
            RefreshAllMeters();
        });
    }

    private void OnAccentChanged()
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTrayIcon(_currentGaugePercent, _currentTooltip);
            RefreshAllMeters();
        });
    }

    private void RefreshAllMeters()
    {
        foreach (var kvp in _scheduler.Current)
        {
            ApplySnapshot(kvp.Value);
        }
    }

    public void UpdateTrayIcon(double percent, string? tooltip = null)
    {
        bool isLightTheme = WindowsThemeListener.IsTaskbarLightTheme();

        uint dpi = NativeMethods.GetDpiForSystem();
        if (dpi == 0) dpi = 96;

        int iconSize = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        if (iconSize <= 0) iconSize = (int)Math.Round(16 * (dpi / 96.0));

        tooltip ??= _currentTooltip;

        IntPtr iconHandle = TrayIconRenderer.CreateGaugeIconHandle(percent, isLightTheme, iconSize);
        IntPtr previousHandle = _currentIconHandle;
        Icon? previousIcon = _currentIcon;

        _currentIconHandle = iconHandle;
        _currentIcon = iconHandle == IntPtr.Zero ? null : Icon.FromHandle(iconHandle);

        // Once yeni ikon kabuga verilir, sonra onceki handle yok edilir.
        _tray.UpdateIcon(iconHandle);
        _tray.UpdateTooltip(tooltip);

        previousIcon?.Dispose();
        if (previousHandle != IntPtr.Zero && previousHandle != iconHandle)
        {
            NativeMethods.DestroyIcon(previousHandle);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _lastDeactivatedTime = DateTimeOffset.UtcNow;
            HideFlyout();
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideFlyout();
            e.Handled = true;
        }
    }

    public void Toggle()
    {
        if (DateTimeOffset.UtcNow - _lastDeactivatedTime < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        if (_isVisible)
        {
            HideFlyout();
        }
        else
        {
            ShowFlyout();
        }
    }

    public void ShowFlyout()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        double scale = dpi / 96.0;

        int targetWidth = (int)Math.Round(380 * scale);

        // 4d: Pencere boyutu içeriğe göre dinamik uzasın (Maksimum ekranın %70'i)
        RootLayout.Measure(new Windows.Foundation.Size(380, double.PositiveInfinity));
        double desiredHeight = RootLayout.DesiredSize.Height;
        if (desiredHeight <= 0) desiredHeight = 390;

        // Imlec konumuna dus: tiklamayla acarken zaten dogru sonucu verir.
        // GUID ile Shell_NotifyIconGetRect denemeye gerek yok.
        var (tempX, tempY) = FlyoutPositioner.CalculatePosition(Guid.Empty, _hwnd, 0, targetWidth, (int)Math.Round(desiredHeight * scale));
        var pt = new NativeMethods.POINT { X = tempX, Y = tempY };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
        NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo);

        int workHeight = monitorInfo.rcWork.Height;
        int maxHeight = (int)Math.Round(workHeight * 0.70);

        int targetHeight = Math.Min((int)Math.Round((desiredHeight + 12) * scale), maxHeight);

        // 1 & 4d: imlec konumundan hizala, calisma alanina kirp
        var (x, y) = FlyoutPositioner.CalculatePosition(Guid.Empty, _hwnd, 0, targetWidth, targetHeight);

        EfficiencyModeManager.SetEfficiencyMode(false);
        _appWindow.MoveAndResize(new RectInt32(x, y, targetWidth, targetHeight));
        _appWindow.Show();
        this.Activate();
        NativeMethods.SetForegroundWindow(_hwnd);

        _isVisible = true;

        PlayEntranceAnimation();
    }

    private void PlayEntranceAnimation()
    {
        var visual = ElementCompositionPreview.GetElementVisual(RootLayout);
        var compositor = visual.Compositor;

        bool animationsEnabled = true;
        try
        {
            animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch { }

        if (animationsEnabled)
        {
            var slideAnim = compositor.CreateScalarKeyFrameAnimation();
            slideAnim.Duration = TimeSpan.FromMilliseconds(150);
            var easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.0f, 0.0f),
                new Vector2(0.0f, 1.0f));
            slideAnim.InsertKeyFrame(0.0f, 12.0f);
            slideAnim.InsertKeyFrame(1.0f, 0.0f, easing);

            var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
            fadeAnim.Duration = TimeSpan.FromMilliseconds(150);
            fadeAnim.InsertKeyFrame(0.0f, 0.0f);
            fadeAnim.InsertKeyFrame(1.0f, 1.0f);

            visual.StartAnimation("Translation.Y", slideAnim);
            visual.StartAnimation("Opacity", fadeAnim);
        }
        else
        {
            RootLayout.Translation = Vector3.Zero;
            visual.Opacity = 1.0f;
        }
    }

    public void HideFlyout()
    {
        if (!_isVisible) return;

        // Once bayrak, sonra Hide: Hide yeni bir Deactivated tetikleyip geri girebilir.
        _isVisible = false;
        var visual = ElementCompositionPreview.GetElementVisual(RootLayout);
        visual.Opacity = 0.0f;
        _appWindow.Hide();
        EfficiencyModeManager.SetEfficiencyMode(true);
    }

    private void OnSnapshotUpdated(UsageSnapshot snapshot)
    {
        this.DispatcherQueue.TryEnqueue(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(UsageSnapshot snapshot)
    {
        if (snapshot.ProviderId.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            UpdateClaudeCard(snapshot);
        }
        else if (snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            UpdateCodexCard(snapshot);
        }

        LastUpdatedText.Text = $"{snapshot.FetchedAt.ToLocalTime():HH:mm:ss} güncellendi";
        RecalculateTrayIcon();
    }

    private void UpdateClaudeCard(UsageSnapshot snapshot)
    {
        QuotaVisuals.ApplyPlan(ClaudePlanBadge, ClaudePlanText, snapshot.PlanName);

        // 4a: Hata durumunda sayaçlar gizlenir, tek satırlık temiz hata ve eylem paneli açılır
        if (snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error)
        {
            ClaudeStatusText.Text = snapshot.Status == ProviderStatus.AuthRequired ? "Giriş Gerekli" : "Hata";
            ClaudeMetersPanel.Visibility = Visibility.Collapsed;
            ClaudeErrorPanel.Visibility = Visibility.Visible;

            if (snapshot.Status == ProviderStatus.AuthRequired)
            {
                ClaudeErrorTitle.Text = "Oturum Süresi Doldu";
                ClaudeErrorDetail.Text = "Claude Code CLI ile tekrar giriş yapın.";
            }
            else
            {
                ClaudeErrorTitle.Text = "Kota Alınamadı";
                ClaudeErrorDetail.Text = snapshot.StaleReason ?? "Sunucudan geçerli veri alınamadı.";
            }
            return;
        }

        ClaudeErrorPanel.Visibility = Visibility.Collapsed;
        ClaudeMetersPanel.Visibility = Visibility.Visible;

        var sessionWindow = snapshot.Windows.FirstOrDefault(w => w.Kind == WindowKind.Session)
            ?? snapshot.Windows.FirstOrDefault(w => w.Label?.Contains("saat", StringComparison.OrdinalIgnoreCase) == true)
            ?? snapshot.Windows.FirstOrDefault();

        var weeklyWindow = snapshot.Windows.FirstOrDefault(w => w.Kind == WindowKind.Weekly)
            ?? snapshot.Windows.FirstOrDefault(w => w.Label?.Contains("hafta", StringComparison.OrdinalIgnoreCase) == true);

        if (ReferenceEquals(weeklyWindow, sessionWindow)) weeklyWindow = null;

        QuotaVisuals.Apply(sessionWindow, ClaudeProgressBar, ClaudePrimaryPercentText, ClaudeResetText, ClaudePrimaryLabelText, "Claude 5 saatlik kota");
        QuotaVisuals.Apply(weeklyWindow, ClaudeWeeklyProgressBar, ClaudeWeeklyPercentText, ClaudeWeeklyResetText, null, "Claude haftalık kota");

        ClaudeStatusText.Text = snapshot is { Status: ProviderStatus.Degraded, StaleReason: not null }
            ? snapshot.StaleReason
            : string.Empty;
    }

    private void UpdateCodexCard(UsageSnapshot snapshot)
    {
        QuotaVisuals.ApplyPlan(CodexPlanBadge, CodexPlanText, snapshot.PlanName);

        // 4a: Hata durumunda sayaçlar gizlenir, tek satırlık temiz hata ve eylem paneli açılır
        if (snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error)
        {
            CodexStatusText.Text = snapshot.Status == ProviderStatus.AuthRequired ? "Giriş Gerekli" : "Hata";
            CodexMetersPanel.Visibility = Visibility.Collapsed;
            CodexErrorPanel.Visibility = Visibility.Visible;

            if (snapshot.Status == ProviderStatus.AuthRequired)
            {
                CodexErrorTitle.Text = "Oturum Süresi Doldu";
                CodexErrorDetail.Text = "Codex CLI ile tekrar giriş yapın.";
            }
            else
            {
                CodexErrorTitle.Text = "Kota Alınamadı";
                CodexErrorDetail.Text = snapshot.StaleReason ?? "Sunucudan geçerli veri alınamadı.";
            }
            return;
        }

        CodexErrorPanel.Visibility = Visibility.Collapsed;
        CodexMetersPanel.Visibility = Visibility.Visible;

        var primary = snapshot.Windows.FirstOrDefault(w => w.Kind == WindowKind.Session)
            ?? snapshot.Windows.FirstOrDefault(w => w.Label?.Contains("saat", StringComparison.OrdinalIgnoreCase) == true)
            ?? snapshot.Windows.FirstOrDefault();

        var secondary = snapshot.Windows.FirstOrDefault(w => w.Kind == WindowKind.Weekly)
            ?? snapshot.Windows.FirstOrDefault(w => w.Label?.Contains("hafta", StringComparison.OrdinalIgnoreCase) == true);

        if (ReferenceEquals(secondary, primary)) secondary = null;

        QuotaVisuals.Apply(primary, CodexProgressBar, CodexPrimaryPercentText, CodexPrimaryResetText, CodexPrimaryLabelText, "Codex 5 saatlik kota");
        QuotaVisuals.Apply(secondary, CodexSecondaryProgressBar, CodexSecondaryPercentText, CodexSecondaryResetText, CodexSecondaryLabelText, "Codex haftalık kota");

        // 4b: Anlamsız "Kota: %0" satırı kaldırıldı
        CodexStatusText.Text = snapshot is { Status: ProviderStatus.Degraded, StaleReason: not null }
            ? snapshot.StaleReason
            : string.Empty;

        var additional = snapshot.Windows.FirstOrDefault(w => w.Label?.Contains("gpt-", StringComparison.OrdinalIgnoreCase) == true);
        if (additional is null)
        {
            CodexReserveStackPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            CodexReserveStackPanel.Visibility = Visibility.Visible;
            QuotaVisuals.Apply(additional, CodexReserveProgressBar, CodexReservePercentText, CodexReserveResetText, CodexReserveLabelText, "Codex ek kota");
        }
    }

    private void RecalculateTrayIcon()
    {
        double gaugePercent = 0.0;
        var parts = new List<string>();

        foreach (var (id, name) in new[] { ("claude", "Claude"), ("codex", "Codex") })
        {
            if (!_scheduler.Current.TryGetValue(id, out var snapshot)) continue;

            if (snapshot is not { Status: ProviderStatus.Ok or ProviderStatus.Degraded } || snapshot.Windows.Count == 0)
            {
                if (snapshot.Status == ProviderStatus.AuthRequired) parts.Add($"{name}: Giriş Gerekli");
                continue;
            }

            // 3. TRAY İKONUNDA HANGİ SAYININ GÖSTERİLECEĞİ:
            // Model bazlı ek limitler (gpt-reserve vb.) tray hesabından ÇIKARILIR.
            // Yalnızca ana kotalar (Session ve Weekly) hesaba katılır.
            var mainWindows = snapshot.Windows
                .Where(w => w.Kind is WindowKind.Session or WindowKind.Weekly &&
                            !(w.Label != null && w.Label.Contains("gpt-", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (mainWindows.Count > 0)
            {
                var max = mainWindows.Max(w => w.Percent);
                gaugePercent = Math.Max(gaugePercent, max);
                parts.Add($"{name} %{max:F0}");
            }
        }

        // 3. Tooltip: "Claude %62 · Codex %69" gibi tek satır, net ve ayrıntısız
        var tooltip = parts.Count > 0 ? string.Join(" · ", parts) : "Kalan";

        _currentGaugePercent = gaugePercent;
        _currentTooltip = tooltip;
        UpdateTrayIcon(gaugePercent, tooltip);
    }
}
