using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Windows.Graphics;
using Windows.System;
using Kalan.Core.Abstractions;
using Kalan.Core.Cost;
using Kalan.Core.Diagnostics;
using Kalan.Core.Model;
using Kalan.Core.Providers;
using Kalan.Core.Refresh;
using Kalan.Core.Usage;
using Kalan.Platform.Windows.Interop;
using Kalan.Platform.Windows.Power;
using Kalan.Platform.Windows.Providers;
using Kalan.Platform.Windows.Theme;
using Kalan.Platform.Windows.Tray;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Kalan.App.Views;

public sealed partial class FlyoutWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly SystemTrayHost _tray;
    private readonly TrayMenuWindow _menu;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly RefreshScheduler _scheduler;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private DateTimeOffset _lastDeactivatedTime = DateTimeOffset.MinValue;
    private bool _isVisible;
    private double? _currentGaugePercent;
    private string _currentTooltip = "Kalan: Veri yok";
    private string? _trayProviderId;
    private SettingsWindow? _settingsWindow;
    private IntPtr _currentIconHandle = IntPtr.Zero;
    private Icon? _currentIcon;
    private bool _selfTestExitInProgress;

    /// <summary>
    /// Self-test için çıkış eylemini bir kez gözlemleyip gerekirse ertelemeyi
    /// sağlayan kanca. Normal çalışmada null kalır; menü davranışını değiştirmez.
    /// </summary>
    public Func<Task<bool>>? SelfTestExitRequested { get; set; }

    // Sekme şeridi durumu. Tray'e dokunulmaz; ikon hesabı RecalculateTrayIcon'da aynen durur.
    private sealed record TabDef(
        string Id,
        string Name,
        string? PathData = null,
        string? AssetUri = null,
        string? BrandHex = null);

    // Gerçek sağlayıcı işaretleri. Bu sözlük sağlayıcı bileşimini kurmaz;
    // yalnızca registry'den gelen sağlayıcılar için görsel sunum bilgisidir.
    // - Claude/OpenCode: Simple Icons v16 (CC0); kayıt hex'leri Claude #D97757,
    //   OpenCode #000000.
    // - Codex/OpenAI: gerçek tek renk path; aktifken TextFillColorPrimaryBrush.
    // - Antigravity: Google'ın resmi tek renk basın paketi varlığı (Assets/ProviderAntigravity.png)
    private const string ClaudePathData =
        "m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z";

    private const string CodexPathData =
        "M8.086.457a6.105 6.105 0 013.046-.415c1.333.153 2.521.72 3.564 1.7a.117.117 0 00.107.029c1.408-.346 2.762-.224 4.061.366l.063.03.154.076c1.357.703 2.33 1.77 2.918 3.198.278.679.418 1.388.421 2.126a5.655 5.655 0 01-.18 1.631.167.167 0 00.04.155 5.982 5.982 0 011.578 2.891c.385 1.901-.01 3.615-1.183 5.14l-.182.22a6.063 6.063 0 01-2.934 1.851.162.162 0 00-.108.102c-.255.736-.511 1.364-.987 1.992-1.199 1.582-2.962 2.462-4.948 2.451-1.583-.008-2.986-.587-4.21-1.736a.145.145 0 00-.14-.032c-.518.167-1.04.191-1.604.185a5.924 5.924 0 01-2.595-.622 6.058 6.058 0 01-2.146-1.781c-.203-.269-.404-.522-.551-.821a7.74 7.74 0 01-.495-1.283 6.11 6.11 0 01-.017-3.064.166.166 0 00.008-.074.115.115 0 00-.037-.064 5.958 5.958 0 01-1.38-2.202 5.196 5.196 0 01-.333-1.589 6.915 6.915 0 01.188-2.132c.45-1.484 1.309-2.648 2.577-3.493.282-.188.55-.334.802-.438.286-.12.573-.22.861-.304a.129.129 0 00.087-.087A6.016 6.016 0 015.635 2.31C6.315 1.464 7.132.846 8.086.457zm-.804 7.85a.848.848 0 00-1.473.842l1.694 2.965-1.688 2.848a.849.849 0 001.46.864l1.94-3.272a.849.849 0 00.007-.854l-1.94-3.393zm5.446 6.24a.849.849 0 000 1.695h4.848a.849.849 0 000-1.696h-4.848Z";

    private sealed record IconDef(
        string? PathData = null,
        string? AssetUri = null,
        string? BrandHex = null);

    private static readonly IReadOnlyDictionary<string, IconDef> ProviderIconDefinitions =
        new Dictionary<string, IconDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new(ClaudePathData, BrandHex: "D97757"),
            ["codex"] = new(CodexPathData),
            ["antigravity"] = new(AssetUri: "ms-appx:///Assets/ProviderAntigravity.png"),
            ["opencode"] = new(PathData: "M22 24H2V0h20zM17 4.8H7v14.4h10z"),
        };
    private const string FallbackTabPathData = "M2,8 L8,2 L14,8 L8,14 Z M5,8 H11 V10 H5 Z";
    private readonly Dictionary<string, (ToggleButton Button, ProgressBar Meter)> _tabs = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedId = "claude";
    private bool _userPickedTab;

    // Maliyet: yerel JSONL taraması, thread pool'da. Bayat koşular çöpe gider.
    private long _costRun;
    private string? _costForId;
    private DateTimeOffset _costAt = DateTimeOffset.MinValue;

    // Flyout genişliği sabit 360px (DPI ölçekli); yükseklik içeriğe göre ayarlanır.
    private int _targetWidth;

    public double? CurrentClaudePercent => _currentGaugePercent;

    public FlyoutWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Ortak popover kabuğu: akrilik, başlıksız sunucu, switcher gizliliği.
        PopoverHelper.ConfigureChrome(this, _appWindow, _hwnd);
        PopoverHelper.ConfigureDismissal(this, _appWindow, HideFlyout, RootLayout,
            onDeactivated: () => _lastDeactivatedTime = DateTimeOffset.UtcNow);

        // Immersive Dark Mode
        UpdateWindowFrameTheme();

        // Subclass hook: WM_DWMCOLORIZATIONCOLORCHANGED (0x0320) ve WM_SETTINGCHANGE (0x001A)
        _subclassProc = WindowSubclassProc;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, new UIntPtr(1), IntPtr.Zero);

        // Işıkla kapanma (odak kaybı/Esc/kapatma) ortak tabanda; zaman damgası
        // aynı tıklamanın pencereyi kapatıp hemen yeniden açmasını önler.

        // Initialize HTTP Client and RefreshScheduler
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Kalan/0.1");

        _providers = ProviderRegistry.CreateAll(_http, AntigravityProcessPortFinder.FindPorts);
        _selectedId = _providers.FirstOrDefault()?.Id ?? "claude";

        _scheduler = new RefreshScheduler(_providers, options: new RefreshOptions
        {
            Interval = TimeSpan.FromMinutes(5),
            MaxJitter = TimeSpan.FromSeconds(5),
            EmitCachedOnStart = true,
        });

        _scheduler.SnapshotUpdated += OnSnapshotUpdated;

        // Wire window refresh button
        RefreshButton.Click += (s, e) => _ = RefreshManuallyAsync();

        SettingsButton.Click += (s, e) => OpenSettingsWindow();
        ExitButton.Click += (s, e) => ExitApplication();

        BuildTabs();

        // Menü native WinUI penceresidir (TrayMenuWindow); WinForms menüsü yok.
        // Önce menü kurulur (tray lambdaları ona kapanır).
        _menu = new TrayMenuWindow();
        _menu.RefreshRequested += () => _ = RefreshManuallyAsync();
        _menu.SettingsRequested += () => this.DispatcherQueue.TryEnqueue(OpenSettingsWindow);
        _menu.ExitRequested += () => this.DispatcherQueue.TryEnqueue(ExitApplication);

        // Tray: WinForms NotifyIcon (saglam yol). Ikon HICON olarak uretilir,
        // sahiplik SystemTrayHost'a gecer: once yeni ikon kabuga verilir,
        // sonra onceki handle yok edilir (bkz. UpdateTrayIcon).
        _tray = new SystemTrayHost();
        _tray.LeftClicked += () => this.DispatcherQueue.TryEnqueue(() =>
        {
            // Menü açıkken sol tık: menü kapanıp flyout açılır (ikisi aynı anda durmaz).
            if (_menu.IsMenuVisible)
            {
                _menu.HideMenu();
                ShowFlyout();
            }
            else
            {
                Toggle();
            }
        });
        _tray.RightClicked += () => this.DispatcherQueue.TryEnqueue(ToggleMenu);

        UpdateTrayIcon(_currentGaugePercent, _currentTooltip);

        Trace.Info("tray", "initialized");

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
        PopoverHelper.HidePopover(_appWindow, RootLayout);
        Trace.Info("window", "flyout.hide");
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.TrayProviderChanged += OnTrayProviderChanged;
        }
        _settingsWindow.ShowAndFocus();
    }

    private async Task RefreshManuallyAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await _scheduler.RefreshAllAsync();
        }
        catch (OperationCanceledException)
        {
            // Exit sırasında scheduler iptal edilir; async-void Click içine
            // TaskCanceledException sızıp XAML unhandled akışını tetiklemesin.
            Trace.Info("provider.refresh", "manual status=cancelled");
        }
        catch (ObjectDisposedException)
        {
            // Kapanışta başka bir manuel tur scheduler dispose yarışına girebilir;
            // bu da kullanıcı eylemi değil, kontrollü kapanış durumudur.
            Trace.Info("provider.refresh", "manual status=cancelled");
        }
        catch (Exception ex)
        {
            Trace.Error("provider.refresh", $"manual status=exception type={ex.GetType().Name}");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }

        RefreshCost(force: true);
    }

    private void OnTrayProviderChanged(string? providerId)
    {
        _trayProviderId = providerId;
        RecalculateTrayIcon();
    }

    private void ExitApplication()
    {
        Trace.Info("app", "exit-requested");
        if (_selfTestExitInProgress)
        {
            return;
        }

        if (SelfTestExitRequested is { } selfTestExit)
        {
            _selfTestExitInProgress = true;
            _ = CompleteSelfTestExitAsync(selfTestExit);
            return;
        }

        ExitApplicationCore();
    }

    private async Task CompleteSelfTestExitAsync(Func<Task<bool>> selfTestExit)
    {
        bool shouldExit;
        try
        {
            shouldExit = await selfTestExit();
        }
        catch (Exception ex)
        {
            Trace.Error("selftest", $"exit-hook-failed type={ex.GetType().Name}");
            Environment.ExitCode = 1;
            shouldExit = true;
        }
        finally
        {
            _selfTestExitInProgress = false;
        }

        if (shouldExit)
        {
            ExitApplicationCore();
        }
    }

    private void ExitApplicationCore()
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
        UpdateTabs();
        RenderDetail();
    }

    // ---- Sekme şeridi: ToggleButton + altında 2px mini ölçer. SelectorBar mini ölçeri
    // barındıramadığı için kullanılmadı; sağlayıcı sayısı az (2-6), StackPanel yeter. ----

    private void BuildTabs()
    {
        TabStrip.Children.Clear();
        _tabs.Clear();

        foreach (var provider in _providers) EnsureTab(provider.Id, provider.DisplayName);
        UpdateTabs();
    }

    private void EnsureTab(string id, string? name = null)
    {
        if (_tabs.ContainsKey(id)) return;

        var known = GetProviderTab(id, name);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(CreateProviderIcon(known));
        var nameText = new TextBlock
        {
            Text = known.Name,
            VerticalAlignment = VerticalAlignment.Center,
        };
        QuotaVisuals.SetTextStyle(nameText, "CaptionTextBlockStyle");
        row.Children.Add(nameText);

        var meter = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = QuotaVisuals.Fill("SubtleFillColorTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(row);
        content.Children.Add(meter);

        var button = new ToggleButton
        {
            Content = content,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 6, 4, 6),
            CornerRadius = QuotaVisuals.PillCorner(),
        };
        var capturedId = id;
        button.Click += (s, e) => SelectProvider(capturedId);

        // Varsayılan ToggleButton checked stili accent basar; tasarım ince gri ister.
        // Tema anahtarlarını düğüm sözlüğünde ezmek şablon yazmaktan kısadır.
        button.Resources["ToggleButtonBackgroundChecked"] = QuotaVisuals.Fill("SubtleFillColorSecondaryBrush");
        button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = QuotaVisuals.Fill("SubtleFillColorTertiaryBrush");
        button.Resources["ToggleButtonBackgroundCheckedPressed"] = QuotaVisuals.Fill("SubtleFillColorTertiaryBrush");
        var primaryText = QuotaVisuals.Fill("TextFillColorPrimaryBrush");
        button.Resources["ToggleButtonForegroundChecked"] = primaryText;
        button.Resources["ToggleButtonForegroundCheckedPointerOver"] = primaryText;
        button.Resources["ToggleButtonForegroundCheckedPressed"] = primaryText;

        _tabs[id] = (button, meter);
        TabStrip.Children.Add(button);
    }

    private void SelectProvider(string id)
    {
        _userPickedTab = true;
        _selectedId = id;
        UpdateTabs();
        RenderDetail();
        EnqueueResize();
        RefreshCost(force: false);
    }

    public bool SelectProviderForVerification(string providerId)
    {
        var provider = _providers.FirstOrDefault(
            p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return false;

        SelectProvider(provider.Id);
        Trace.Info("verification", $"provider-selected provider={provider.Id}");
        return true;
    }

    private void UpdateTabs()
    {
        var selectedFill = QuotaVisuals.Fill("SubtleFillColorSecondaryBrush");
        var clear = new SolidColorBrush(Colors.Transparent);

        foreach (var (id, (button, meter)) in _tabs)
        {
            var selected = id.Equals(_selectedId, StringComparison.OrdinalIgnoreCase);
            button.IsChecked = selected;
            button.Background = selected ? selectedFill : clear;

            if (button.Content is StackPanel content
                && content.Children.FirstOrDefault() is StackPanel iconRow
                && iconRow.Children.FirstOrDefault() is UIElement icon)
            {
                SetProviderIconBrush(
                    icon,
                    ProviderIconBrush(GetProviderTab(id), selected));
            }

            if (!_scheduler.Current.TryGetValue(id, out var snapshot))
            {
                meter.Value = 0;
                meter.Foreground = QuotaVisuals.Fill("ControlStrongFillColorDisabledBrush");
                continue;
            }

            if (snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error)
            {
                // Hata: mini ölçer yerine critical renginde 2px dolu çizgi.
                meter.Value = 100;
                meter.Foreground = QuotaVisuals.Fill("SystemFillColorCriticalBrush");
                continue;
            }

            if (snapshot.Status == ProviderStatus.NotInstalled || snapshot.Windows.Count == 0)
            {
                meter.Value = 0;
                meter.Foreground = QuotaVisuals.Fill("ControlStrongFillColorDisabledBrush");
                AutomationProperties.SetName(button, $"{snapshot.ProviderId}, veri yok");
                continue;
            }

            var percent = MainPercent(snapshot);
            meter.Value = percent;
            meter.Foreground = QuotaVisuals.MeterBrush(percent);
            AutomationProperties.SetName(button, $"{snapshot.ProviderId}, yüzde {percent:F0}");
        }
    }

    /// <summary>Tray ile aynı kural: model bazlı ek limitler (gpt-*) sekme ölçerine girmez.</summary>
    private static double MainPercent(UsageSnapshot snapshot)
    {
        double max = 0;
        foreach (var w in snapshot.Windows)
        {
            if (w.Kind is not (WindowKind.Session or WindowKind.Weekly)) continue;
            if (w.Label is not null && w.Label.Contains("gpt-", StringComparison.OrdinalIgnoreCase)) continue;
            if (w.Percent > max) max = w.Percent;
        }
        return max;
    }

    // ---- Tek sağlayıcı detayı ----

    private void OnSnapshotUpdated(UsageSnapshot snapshot)
    {
        this.DispatcherQueue.TryEnqueue(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(UsageSnapshot snapshot)
    {
        EnsureTab(snapshot.ProviderId);
        MaybeAutoSelect();
        UpdateTabs();
        RenderDetail();
        EnqueueResize();
        RecalculateTrayIcon();
    }

    /// <summary>
    /// Kullanıcı henüz sekme seçmediyse verisi olan ilk sağlayıcıyı göster.
    /// (Örn. Claude 429 yerken Codex doluysa açılışta hata sayfası gösterilmez.)
    /// Kullanıcı bir kez tıkladı mı seçim ona aittir, bir daha ellemeyiz.
    /// </summary>
    private void MaybeAutoSelect()
    {
        if (_userPickedTab) return;

        if (_scheduler.Current.TryGetValue(_selectedId, out var current)
            && current.Windows.Count > 0
            && current.Status is not (ProviderStatus.AuthRequired or ProviderStatus.NotInstalled or ProviderStatus.Error))
        {
            return;
        }

        var pick = _scheduler.Current.Values
            .FirstOrDefault(s => s.Windows.Count > 0 && s.Status is not (ProviderStatus.AuthRequired or ProviderStatus.NotInstalled or ProviderStatus.Error))
            ?? _scheduler.Current.Values.FirstOrDefault();

        if (pick is not null && !pick.ProviderId.Equals(_selectedId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedId = pick.ProviderId;
        }
    }

    private void RenderDetail()
    {
        if (!_scheduler.Current.TryGetValue(_selectedId, out var snapshot))
        {
            DetailName.Text = TabDisplayName(_selectedId);
            DetailIconHost.Content = CreateProviderIcon(GetProviderTab(_selectedId), active: true);
            DetailUpdated.Text = "Bekleniyor…";
            DetailError.Visibility = Visibility.Collapsed;
            DetailUnavailable.Visibility = Visibility.Collapsed;
            DetailWindows.Children.Clear();
            CostSection.Visibility = Visibility.Collapsed;
            return;
        }

        DetailName.Text = TabDisplayName(snapshot.ProviderId);
        DetailIconHost.Content = CreateProviderIcon(GetProviderTab(snapshot.ProviderId), active: true);
        QuotaVisuals.ApplyPlan(DetailPlanBadge, DetailPlanText, snapshot.PlanName);

        // Bayat veri gösteriliyorsa sebep üstte tek satır yazar
        // (örn. hız sınırı + kaç dk önceki veri); taze veride tazelik saati.
        DetailUpdated.Text = snapshot is { Status: ProviderStatus.Degraded, StaleReason: not null }
            ? snapshot.StaleReason
            : QuotaVisuals.FormatUpdated(snapshot.FetchedAt);

        if (snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error)
        {
            DetailUnavailable.Visibility = Visibility.Collapsed;
            DetailErrorTitle.Text = snapshot.Status == ProviderStatus.AuthRequired ? "Oturum Süresi Doldu" : "Kota Alınamadı";
            DetailErrorDetail.Text = snapshot.Status == ProviderStatus.AuthRequired
                ? $"{TabDisplayName(snapshot.ProviderId)} CLI ile tekrar giriş yapın."
                : snapshot.StaleReason ?? "Sunucudan geçerli veri alınamadı.";
            DetailError.Visibility = Visibility.Visible;
            DetailWindows.Children.Clear();
        }
        else if (snapshot.Status == ProviderStatus.NotInstalled)
        {
            DetailError.Visibility = Visibility.Collapsed;
            DetailUnavailableTitle.Text = "Kurulu değil veya açık değil";
            DetailUnavailableDetail.Text = snapshot.StaleReason ?? "Antigravity açık değil.";
            DetailUnavailable.Visibility = Visibility.Visible;
            DetailWindows.Children.Clear();
        }
        else if (snapshot.Windows.Count == 0)
        {
            DetailError.Visibility = Visibility.Collapsed;
            DetailUnavailableTitle.Text = "Veri yok";
            DetailUnavailableDetail.Text = snapshot.StaleReason ?? "Sağlayıcıdan kullanılabilir kota alınamadı.";
            DetailUnavailable.Visibility = Visibility.Visible;
            DetailWindows.Children.Clear();
        }
        else
        {
            DetailError.Visibility = Visibility.Collapsed;
            DetailUnavailable.Visibility = Visibility.Collapsed;
            RenderWindows(snapshot);
        }
    }

    private string TabDisplayName(string providerId) =>
        _providers.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? providerId;

    private TabDef GetProviderTab(string providerId, string? fallbackName = null)
    {
        var name = _providers.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? fallbackName
            ?? providerId;

        return ProviderIconDefinitions.TryGetValue(providerId, out var icon)
            ? new TabDef(providerId, name, icon.PathData, icon.AssetUri, icon.BrandHex)
            : new TabDef(providerId, name, FallbackTabPathData);
    }

    private static Microsoft.UI.Xaml.Media.Brush ProviderIconBrush(TabDef tab, bool active)
    {
        if (!active)
        {
            return QuotaVisuals.Fill("TextFillColorTertiaryBrush");
        }

        if (string.IsNullOrWhiteSpace(tab.BrandHex))
        {
            return QuotaVisuals.Fill("TextFillColorPrimaryBrush");
        }

        var hex = tab.BrandHex;
        var color = Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
        return new SolidColorBrush(color);
    }

    private static UIElement CreateProviderIcon(TabDef tab, bool active = false)
    {
        var brush = ProviderIconBrush(tab, active);
        if (tab.AssetUri is not null)
        {
            var bitmap = new BitmapIcon
            {
                UriSource = new Uri(tab.AssetUri),
                ShowAsMonochrome = true,
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleX = 1.2, ScaleY = 1.2 },
            };
            bitmap.Foreground = brush;
            return new Viewbox
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Child = bitmap,
            };
        }

        var path = (XamlPath)XamlReader.Load(
            $"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{tab.PathData ?? FallbackTabPathData}\" Width=\"24\" Height=\"24\" Stretch=\"Uniform\" />");
        path.Fill = brush;
        return new Viewbox
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Child = path,
        };
    }

    private static void SetProviderIconBrush(UIElement icon, Microsoft.UI.Xaml.Media.Brush brush)
    {
        if (icon is not Viewbox viewbox) return;

        switch (viewbox.Child)
        {
            case XamlPath path:
                path.Fill = brush;
                break;
            case BitmapIcon bitmap:
                bitmap.Foreground = brush;
                break;
        }
    }

    private void RenderWindows(UsageSnapshot snapshot)
    {
        DetailWindows.Children.Clear();
        var now = DateTimeOffset.UtcNow;

        foreach (var window in snapshot.Windows)
        {
            var block = new StackPanel { Spacing = 6 };

            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(window.Label) ? KindName(window.Kind) : window.Label,
                TextWrapping = TextWrapping.Wrap,
            };
            QuotaVisuals.SetTextStyle(label, "BodyStrongTextBlockStyle");
            block.Children.Add(label);

            var bar = new ProgressBar
            {
                Value = window.Percent,
                Maximum = 100,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = QuotaVisuals.Fill("SubtleFillColorTertiaryBrush"),
                Foreground = QuotaVisuals.MeterBrush(window.Percent),
            };
            AutomationProperties.SetName(bar, $"{label.Text}, yüzde {window.Percent:F0}");
            block.Children.Add(bar);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var percentText = new TextBlock
            {
                Text = $"%{window.Percent:F0} kullanıldı",
                TextWrapping = TextWrapping.Wrap,
            };
            QuotaVisuals.SetTextStyle(percentText, "CaptionTextBlockStyle");
            Grid.SetColumn(percentText, 0);
            row.Children.Add(percentText);

            var reset = QuotaVisuals.FormatReset(window.ResetsAt);
            var resetText = new TextBlock
            {
                Text = string.IsNullOrEmpty(reset) ? string.Empty : reset == "sıfırlandı" ? reset : $"{reset} sonra",
                Foreground = QuotaVisuals.Fill("TextFillColorTertiaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            };
            QuotaVisuals.SetTextStyle(resetText, "CaptionTextBlockStyle");
            Grid.SetColumn(resetText, 1);
            row.Children.Add(resetText);
            block.Children.Add(row);

            if (window.Percent >= 100)
            {
                // Tükendi rozeti: tempo satırı yerine hap.
                var badgeText = new TextBlock
                {
                    Text = PaceCalculator.FormatConsumedBadge(window, now),
                    TextWrapping = TextWrapping.Wrap,
                };
                QuotaVisuals.SetTextStyle(badgeText, "CaptionTextBlockStyle");
                var badge = new Border
                {
                    Background = QuotaVisuals.Fill("SubtleFillColorSecondaryBrush"),
                    CornerRadius = QuotaVisuals.PillCorner(),
                    Padding = new Thickness(6, 1, 6, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = badgeText,
                };
                block.Children.Add(badge);
            }
            else if (PaceCalculator.Calculate(window, now) is { } pace)
            {
                var tempoText = new TextBlock
                {
                    Text = PaceCalculator.Format(window, pace, now),
                    Foreground = QuotaVisuals.Fill("TextFillColorTertiaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                };
                QuotaVisuals.SetTextStyle(tempoText, "CaptionTextBlockStyle");
                block.Children.Add(tempoText);
            }

            DetailWindows.Children.Add(block);
        }
    }

    private static string KindName(WindowKind kind) => kind switch
    {
        WindowKind.Session => "Oturum",
        WindowKind.Weekly => "Haftalık",
        WindowKind.Daily => "Günlük",
        WindowKind.Monthly => "Aylık",
        _ => kind.ToString(),
    };

    // ---- Maliyet özeti: yerel JSONL taraması, thread pool'da; bitince seçiliyse yaz. ----

    private void RefreshCost(bool force)
    {
        var id = _selectedId;
        if (!force && id.Equals(_costForId, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - _costAt < TimeSpan.FromMinutes(5))
        {
            return;
        }

        var run = ++_costRun;
        Task.Run(() =>
        {
            CostScanResult ScanToday(string pid) => pid switch
            {
                "claude" => ClaudeCostScanner.Scan(new DateTimeOffset(DateTime.Today)),
                "codex" => CodexCostScanner.Scan(new DateTimeOffset(DateTime.Today)),
                _ => new CostScanResult(new TokenTally(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
            };
            CostScanResult ScanMonth(string pid) => pid switch
            {
                "claude" => ClaudeCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-30)),
                "codex" => CodexCostScanner.Scan(DateTimeOffset.UtcNow.AddDays(-30)),
                _ => new CostScanResult(new TokenTally(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
            };
            var pricing = PricingTable.LoadOrEmpty();
            var today = ScanToday(id);
            var month = ScanMonth(id);
            return (
                Today: CostEstimator.Estimate(today, pricing),
                Month: CostEstimator.Estimate(month, pricing),
                Pricing: pricing);
        }).ContinueWith(t =>
        {
            if (run != _costRun) return;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                // Sessiz düşme: tanı için türü yaz (token/icerik asla loglanmaz).
                Trace.Error(
                    "cost",
                    $"scan-failed provider={id} type={t.Exception?.InnerException?.GetType().Name ?? t.Exception?.GetType().Name ?? "unknown"}");
                return;
            }
            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (run != _costRun || !id.Equals(_selectedId, StringComparison.OrdinalIgnoreCase)) return;
                var (today, month, pricing) = t.Result;
                _costForId = id;
                _costAt = DateTimeOffset.UtcNow;
                ApplyCost(today, month, pricing);
            });
        }, TaskScheduler.Default);
    }

    private void ApplyCost(CostReport today, CostReport month, PricingTable pricing)
    {
        var lines = new List<string>(2);

        // Fiyat tablosu boşsa para kısmı GÖSTERİLMEZ; sıfır dolar yanlış bilgidir.
        string Line(string prefix, CostReport report)
        {
            var total = report.InputTokens + report.OutputTokens + report.CacheReadTokens + report.CacheCreationTokens;
            if (total <= 0) return string.Empty;
            return pricing.IsEmpty
                ? $"{prefix} {CompactTokens(total)} token"
                : $"{prefix} {MoneyText(report.TotalCost, pricing.Currency)} · {CompactTokens(total)} token";
        }

        var todayLine = Line("Bugün", today);
        var monthLine = Line("Son 30 gün:", month);

        if (!string.IsNullOrEmpty(todayLine)) lines.Add(todayLine);
        if (!string.IsNullOrEmpty(monthLine)) lines.Add(monthLine);

        if (lines.Count == 0)
        {
            CostSection.Visibility = Visibility.Collapsed;
            return;
        }

        CostSummary.Text = string.Join("\n", lines);
        CostSection.Visibility = Visibility.Visible;
        EnqueueResize();
    }

    private static string CompactTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:F0}M",
        >= 1_000 => $"{tokens / 1_000.0:F0}K",
        _ => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string MoneyText(decimal amount, string currency)
    {
        var symbol = currency.ToUpperInvariant() switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "TRY" => "₺",
            _ => currency + " ",
        };
        return amount >= 100 ? $"{symbol}{amount:F0}" : $"{symbol}{amount:F2}";
    }

    public void UpdateTrayIcon(double? percent, string? tooltip = null)
    {
        bool isLightTheme = WindowsThemeListener.IsTaskbarLightTheme();

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
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
            // Flyout açılırken menü kapanır (ikisi aynı anda durmaz).
            _menu.HideMenu();
            ShowFlyout();
        }
    }

    public void ToggleMenu()
    {
        if (_menu.IsMenuVisible)
        {
            _menu.HideMenu();
        }
        else
        {
            _menu.ShowAtCursor();
        }
    }

    /// <summary>Doğrulama kancası (--menu): tepsi menüsünü providersız açar.</summary>
    public void ShowMenuForVerification() => _menu.ShowAtCursor();
    public TrayMenuWindow MenuWindow => _menu;
    public SettingsWindow? SettingsWindowForSelfTest => _settingsWindow;

    public void ShowFlyout()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        double scale = dpi / 96.0;

        int targetWidth = (int)Math.Round(360 * scale);
        _targetWidth = targetWidth;

        // Pencere boyutu içeriğe göre dinamik uzasın (Maksimum ekranın %70'i)
        RootLayout.Measure(new Windows.Foundation.Size(360, double.PositiveInfinity));
        double desiredHeight = RootLayout.DesiredSize.Height;
        if (desiredHeight <= 0) desiredHeight = 390;

        // Imlec konumuna dus: tiklamayla acarken zaten dogru sonucu verir.
        // GUID ile Shell_NotifyIconGetRect denemeye gerek yok.
        var (tempX, tempY) = FlyoutPositioner.CalculatePosition(Guid.Empty, _hwnd, 0, targetWidth, (int)Math.Round(desiredHeight * scale));
        var pt = new NativeMethods.POINT { X = tempX, Y = tempY };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
        NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo);

        int maxHeight = (int)Math.Round(monitorInfo.rcWork.Height * 0.70);

        int targetHeight = Math.Min((int)Math.Round((desiredHeight + 12) * scale), maxHeight);

        // 1 & 4d: imlec konumundan hizala, calisma alanina kirp
        var (x, y) = FlyoutPositioner.CalculatePosition(
            Guid.Empty,
            _hwnd,
            0,
            targetWidth,
            targetHeight,
            out var edge);

        EfficiencyModeManager.SetEfficiencyMode(false);
        _appWindow.MoveAndResize(new RectInt32(x, y, targetWidth, targetHeight));
        PopoverHelper.ShowPopover(_appWindow, this, _hwnd, RootLayout, edge);

        _isVisible = true;
        Trace.Info("window", "flyout.show");

        RefreshCost(force: false);
    }

    /// <summary>
    /// İçerik değiştikçe (sekme, veri) yüksekliği yeniden ayarla.
    /// Ölçülen yükseklik %70 tavanı aşarsa pencere büyümez, detay ScrollViewer'ı kayar.
    /// Sekme şeridi ve eylem satırı scroll alanının dışındadır, hep görünür.
    /// Yerleşim bir sonraki düşük öncelikli turda yapılır: eklenen çocuklar
    /// ölçülmeden DesiredSize bir kare geriden gelir, pencere kısa kalır.
    /// </summary>
    private void EnqueueResize() =>
        this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ResizeToContent);

    private void ResizeToContent()
    {
        if (!_isVisible || _targetWidth <= 0) return;

        RootLayout.Measure(new Windows.Foundation.Size(_targetWidth, double.PositiveInfinity));
        double desiredHeight = RootLayout.DesiredSize.Height;
        if (desiredHeight <= 0) return;

        int targetHeight = Math.Min((int)Math.Round(desiredHeight + 12), PopoverHelper.WorkAreaMaxHeight());
        if (targetHeight <= 0) return;

        _appWindow.ResizeClient(new SizeInt32(_targetWidth, targetHeight));
    }

    public void HideFlyout()
    {
        if (!_isVisible) return;

        // Once bayrak, sonra Hide: Hide yeni bir Deactivated tetikleyip geri girebilir.
        _isVisible = false;
        PopoverHelper.HidePopover(_appWindow, RootLayout);
        EfficiencyModeManager.SetEfficiencyMode(true);
        Trace.Info("window", "flyout.hide");
    }

    private void RecalculateTrayIcon()
    {
        var available = new Dictionary<string, (string Name, double Percent)>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            var id = provider.Id;
            var name = provider.DisplayName;
            if (!_scheduler.Current.TryGetValue(id, out var snapshot)) continue;

            if (snapshot is not { Status: ProviderStatus.Ok or ProviderStatus.Degraded } || snapshot.Windows.Count == 0) continue;

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
                available[id] = (name, Math.Clamp(max, 0, 100));
            }
        }

        (string Name, double Percent)? selected = null;
        if (_trayProviderId is { } requested)
        {
            if (available.TryGetValue(requested, out var fixedProvider))
            {
                selected = fixedProvider;
            }
        }
        else if (available.Count > 0)
        {
            selected = available.Values.OrderByDescending(value => value.Percent).First();
        }

        double? gaugePercent = selected?.Percent;
        string tooltip = selected is { } value
            ? $"{value.Name} %{value.Percent:F0}"
            : _trayProviderId is { } missing
                ? $"{TabDisplayName(missing)}: Veri yok"
                : "Kalan: Veri yok";

        _currentGaugePercent = gaugePercent;
        _currentTooltip = tooltip;
        UpdateTrayIcon(gaugePercent, tooltip);
    }
}
