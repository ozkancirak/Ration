using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Windows.Graphics;
using Windows.System;
using Ration.Core.Abstractions;
using Ration.Core.Cost;
using Ration.Core.Diagnostics;
using Ration.Core.Model;
using Ration.Core.Providers;
using Ration.Core.Refresh;
using Ration.Core.Usage;
using Ration.Platform.Windows.Interop;
using Ration.Platform.Windows.Power;
using Ration.Platform.Windows.Providers;
using Ration.Platform.Windows.Theme;
using Ration.Platform.Windows.Tray;
using Ration.Platform.Windows.App;

namespace Ration.App.Views;

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
    private static readonly TimeSpan FlyoutRefreshDebounce = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ManualRefreshMinimum = TimeSpan.FromSeconds(10);
    private DateTimeOffset _lastInteractiveRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastManualRefresh = DateTimeOffset.MinValue;
    private bool _isVisible;
    private double? _currentGaugePercent;
    private string _currentTooltip = "Ration: Veri yok";
    private string? _trayProviderId = TrayProviderPreference.Current;
    private SettingsWindow? _settingsWindow;
    private IntPtr _currentIconHandle = IntPtr.Zero;
    private bool _selfTestExitInProgress;

    /// <summary>
    /// Self-test için çıkış eylemini bir kez gözlemleyip gerekirse ertelemeyi
    /// sağlayan kanca. Normal çalışmada null kalır; menü davranışını değiştirmez.
    /// </summary>
    public Func<Task<bool>>? SelfTestExitRequested { get; set; }

    // Sekme şeridi durumu. Tray'e dokunulmaz; ikon hesabı RecalculateTrayIcon'da aynen durur.
    private sealed record TabDef(string Id, string Name, ProviderIconDefinition Icon);
    private readonly Dictionary<string, (ToggleButton Button, ProgressBar Meter)> _tabs = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedId = "claude";
    private bool _userPickedTab;

    // Maliyet: yerel JSONL taraması, thread pool'da. Bayat koşular çöpe gider.
    private long _costRun;
    private string? _costForId;
    private DateTimeOffset _costAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, string> _modelDiagnosticState = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _modelDiagnosticSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private bool _allModelDiagnosticsStarted;

    // Flyout genişliği sabit 380 DIP; yükseklik bütün sekmelerin en uzunu olur.
    private const double FlyoutWidthDip = 380;
    private int _targetWidth;

    // Açılıştaki yerleşim: görev çubuğu alttaysa alt kenar sabit tutulur ki içerik
    // değişince pencere görev çubuğundan kopmasın ya da ekrandan taşmasın.
    private FlyoutEdge _edge = FlyoutEdge.Bottom;
    private int _anchorBottom;

    public double? CurrentClaudePercent => _currentGaugePercent;

    public FlyoutWindow()
    {
        InitializeComponent();
        AppTheme.Apply(RootLayout);

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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ration/0.1");

        _providers = ProviderRegistry.CreateAll(
            _http,
            AntigravityProcessPortFinder.FindPorts,
            AntigravityProcessPortFinder.FindEndpoints);
        _selectedId = _providers.FirstOrDefault()?.Id ?? "claude";

        _scheduler = new RefreshScheduler(_providers, options: new RefreshOptions
        {
            Interval = RefreshIntervalPreference.Current,
            MaxJitter = TimeSpan.FromSeconds(5),
            EmitCachedOnStart = true,
        });

        _scheduler.SnapshotUpdated += OnSnapshotUpdated;

        // Wire window refresh button
        RefreshButton.Click += (s, e) => _ = RefreshManuallyAsync();

        SettingsButton.Click += (s, e) => OpenSettingsWindow();

        BuildTabs();
        UpdateWelcomeState();

        // Menü native WinUI penceresidir (TrayMenuWindow); WinForms menüsü yok.
        // Önce menü kurulur (tray lambdaları ona kapanır).
        _menu = new TrayMenuWindow();
        _menu.RefreshRequested += () => _ = RefreshManuallyAsync();
        _menu.SettingsRequested += () => this.DispatcherQueue.TryEnqueue(OpenSettingsWindow);
        _menu.ExitRequested += () => this.DispatcherQueue.TryEnqueue(ExitApplication);

        // Tray: ham Shell_NotifyIcon (SystemTrayHost). Ikon HICON olarak uretilir,
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

        // Kota bildirimine tıklanınca panel açılır.
        QuotaNotifier.Register(() => this.DispatcherQueue.TryEnqueue(ShowFlyout));

        // Theme listeners
        WindowsThemeListener.ThemeChanged += OnTaskbarThemeChanged;
        AppThemePreference.Changed += OnAppThemeChanged;
        RefreshIntervalPreference.Changed += OnRefreshIntervalChanged;
        EfficiencyModeManager.PauseChanged += OnEfficiencyPauseChanged;
        PricingTableUpdater.Updated += OnPricingUpdated;
        WindowsThemeListener.AccentChanged += OnAccentChanged;
        WindowsThemeListener.DisplayChanged += OnDisplayChanged;

        _appWindow.Resize(new SizeInt32(1, 1));

        // Gizli başlar: CPU önceliği düşük (EcoQoS), ama arka plan yenilemesi çalışır.
        // Yalnızca ekran kilidi / enerji tasarrufu açıksa duraklatılmış başlar.
        if (EfficiencyModeManager.ShouldPause) _scheduler.Pause();
        EfficiencyModeManager.SetEfficiencyMode(true);

        // Start scheduler loop
        _scheduler.Start();
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == SingleInstanceLease.WakeWindowMessageId)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                Trace.Info("single-instance", "wake received");
                ShowFlyout();
            });
            return IntPtr.Zero;
        }

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
        bool isDark = !AppThemePreference.IsAppLightTheme();
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

    public void OpenSettings() => OpenSettingsWindow();

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.TrayProviderChanged += OnTrayProviderChanged;
        }
        _settingsWindow.ShowAndFocus();
    }

    private void OnRefreshIntervalChanged(TimeSpan interval)
    {
        _scheduler.SetInterval(interval);
        Trace.Info("settings", $"refresh-interval minutes={(int)interval.TotalMinutes}");
    }

    private void OnEfficiencyPauseChanged(bool shouldPause)
    {
        if (shouldPause)
        {
            _scheduler.Pause();
        }
        else
        {
            _scheduler.Resume();
        }

        Trace.Info("provider.refresh", $"polling={(shouldPause ? "paused" : "resumed")}");
    }

    private async Task RefreshManuallyAsync()
    {
        if (EfficiencyModeManager.ShouldPause)
        {
            Trace.Info("provider.refresh", "manual status=skipped reason=efficiency");
            return;
        }

        // Yalnızca art arda basmaya karşı: panel açılışındaki otomatik yenileme Yenile
        // düğmesini bloklamaz (önceden ortak damga yüzünden 60 sn sessizce çalışmıyordu).
        var now = DateTimeOffset.UtcNow;
        if (now - _lastManualRefresh < ManualRefreshMinimum)
        {
            Trace.Info("provider.refresh", "manual status=skipped reason=minimum-interval");
            return;
        }

        _lastManualRefresh = now;
        _lastInteractiveRefresh = now;
        RefreshButton.IsEnabled = false;
        try
        {
            Trace.Info("provider.refresh", "manual status=started");
            await _scheduler.RefreshAllAsync(bypassCircuitBreaker: true);
            Trace.Info("provider.refresh", "manual status=completed");
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

    private async Task RefreshOnFlyoutShownAsync()
    {
        if (EfficiencyModeManager.ShouldPause)
        {
            Trace.Info("provider.refresh", "flyout status=skipped reason=efficiency");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastInteractiveRefresh < FlyoutRefreshDebounce)
        {
            Trace.Info("provider.refresh", "flyout status=skipped reason=debounce");
            return;
        }

        _lastInteractiveRefresh = now;
        try
        {
            Trace.Info("provider.refresh", "flyout status=started");
            await _scheduler.RefreshAllAsync();
            Trace.Info("provider.refresh", "flyout status=completed");
        }
        catch (OperationCanceledException)
        {
            Trace.Info("provider.refresh", "flyout status=cancelled");
        }
        catch (ObjectDisposedException)
        {
            Trace.Info("provider.refresh", "flyout status=cancelled");
        }
        catch (Exception ex)
        {
            Trace.Error("provider.refresh", $"flyout status=exception type={ex.GetType().Name}");
        }
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
        AppThemePreference.Changed -= OnAppThemeChanged;
        RefreshIntervalPreference.Changed -= OnRefreshIntervalChanged;
        EfficiencyModeManager.PauseChanged -= OnEfficiencyPauseChanged;
        PricingTableUpdater.Updated -= OnPricingUpdated;

        NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, new UIntPtr(1));

        _tray.Dispose();
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
            if (RestartIfAppThemeChanged()) return;
            // Görev çubuğu teması uygulama temasından bağımsız değişebilir: yalnızca ikon.
            UpdateTrayIcon(_currentGaugePercent, _currentTooltip);
        });
    }

    private void OnAppThemeChanged(AppThemeMode mode)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            if (RestartIfAppThemeChanged()) return;
            UpdateTrayIcon(_currentGaugePercent, _currentTooltip);
        });
    }

    /// <summary>
    /// Uygulama teması yalnızca açılışta ayarlanabildiği için (App.RequestedTheme) etkin
    /// tema değişince süreç kendini yeniden başlatır; Ayarlar açıksa yeniden açılır.
    /// Çalışırken yamalamak sekme yazıları gibi koddan verilen renkleri eski temada bırakıyordu.
    /// </summary>
    private bool RestartIfAppThemeChanged()
    {
        if (AppThemePreference.IsAppLightTheme() == App.AppliedLightTheme) return false;

        var reopenSettings = _settingsWindow?.IsShown == true;
        Trace.Info("app", $"restart reason=theme settings={(reopenSettings ? "open" : "closed")}");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Environment.ProcessPath!,
                SingleInstanceLease.RestartedFlag + (reopenSettings ? " --open-settings" : string.Empty))
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            // Yeniden başlatılamazsa çıkma; kullanıcı elle yeniden başlatana kadar mevcut tema kalır.
            Trace.Error("app", $"restart-failed type={ex.GetType().Name}");
            return false;
        }

        ExitApplicationCore();
        return true;
    }

    private void OnPricingUpdated()
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            if (_isVisible)
            {
                RefreshCost(force: true);
            }
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
        UpdateWelcomeState();
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
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
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
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var content = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        content.Children.Add(row);
        content.Children.Add(meter);
        row.SizeChanged += (_, args) =>
        {
            meter.Width = Math.Max(1, args.NewSize.Width);
        };

        var button = new ToggleButton
        {
            Content = content,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
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
        DetailScrollViewer.ChangeView(null, 0, null);
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
            var tabBrush = ProviderIconBrush(GetProviderTab(id), selected);
            button.IsChecked = selected;
            button.Background = selected ? selectedFill : clear;

            if (button.Content is StackPanel content
                && content.Children.FirstOrDefault() is StackPanel iconRow
                && iconRow.Children.FirstOrDefault() is UIElement icon)
            {
                SetProviderIconBrush(
                    icon,
                    tabBrush);
            }

            if (!_scheduler.Current.TryGetValue(id, out var snapshot))
            {
                meter.Value = 0;
                meter.Foreground = tabBrush;
                continue;
            }

            if (snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error)
            {
                // Hata: mini ölçer yerine critical renginde 2px dolu çizgi.
                meter.Value = 100;
                meter.Foreground = tabBrush;
                continue;
            }

            if (snapshot.Status == ProviderStatus.NotInstalled || snapshot.Windows.Count == 0)
            {
                meter.Value = 0;
                meter.Foreground = tabBrush;
                AutomationProperties.SetName(button, $"{snapshot.ProviderId}, veri yok");
                continue;
            }

            var percent = MainPercent(snapshot);
            meter.Value = 100 - percent;
            meter.Foreground = tabBrush;
            AutomationProperties.SetName(button, $"{snapshot.ProviderId}, yüzde {100 - percent:F0} kaldı");
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
        if (snapshot.ProviderId is "claude" or "codex" or "antigravity" or "opencode")
        {
            _modelDiagnosticSnapshots.Add(snapshot.ProviderId);
        }

        EnsureTab(snapshot.ProviderId);
        MaybeAutoSelect();
        UpdateWelcomeState();
        UpdateTabs();
        RenderDetail();
        RefreshCost(force: false);
        EnqueueResize();
        RecalculateTrayIcon();
        QuotaNotifier.Evaluate(snapshot, TabDisplayName(snapshot.ProviderId));
        MaybeScheduleAllModelDiagnostics();
    }

    private void MaybeScheduleAllModelDiagnostics()
    {
        if (_allModelDiagnosticsStarted || _modelDiagnosticSnapshots.Count < 4) return;

        _allModelDiagnosticsStarted = true;
        var antigravity = _scheduler.Current.TryGetValue("antigravity", out var agy)
            ? agy.Cost
            : null;
        var opencode = _scheduler.Current.TryGetValue("opencode", out var open)
            ? open.Cost
            : null;

        _ = Task.Run(() =>
        {
            try
            {
                var pricing = PricingTable.LoadOrEmpty();
                var periodStart = DateTimeOffset.UtcNow.AddDays(-30);
                var claude = CostEstimator.Estimate(ClaudeCostScanner.Scan(periodStart), pricing);
                var codex = CostEstimator.Estimate(CodexCostScanner.Scan(periodStart), pricing);
                DispatcherQueue.TryEnqueue(() =>
                {
                    LogModelSummary("claude", claude);
                    LogModelSummary("codex", codex);
                    LogModelSummary("antigravity", antigravity);
                    LogModelSummary("opencode", opencode);
                });
            }
            catch (Exception ex)
            {
                Trace.Error("model", $"all-scan-failed type={ex.GetType().Name}");
            }
        });
    }

    private void UpdateWelcomeState()
    {
        var discovered = _scheduler.Current.Values.Any(HasDiscoveredProvider);
        WelcomePanel.Visibility = discovered ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel.Visibility = discovered ? Visibility.Visible : Visibility.Collapsed;
        TabStrip.Visibility = discovered ? Visibility.Visible : Visibility.Collapsed;

        if (discovered || WelcomeProviders.ItemsSource is not null) return;

        WelcomeProviders.ItemsSource = _providers
            .Select(p => $"{p.DisplayName} — {ProviderSearchLocation(p.Id)}")
            .ToList();
    }

    private static bool HasDiscoveredProvider(UsageSnapshot snapshot) =>
        snapshot.Status is ProviderStatus.Ok or ProviderStatus.Degraded
            ? snapshot.Windows.Count > 0 || snapshot.Cost is not null ||
              snapshot.Credits is not null || snapshot.PlanName is not null
            : snapshot.Status is (ProviderStatus.AuthRequired or ProviderStatus.Error) &&
              snapshot.ResolvedVia is not null;

    private static string ProviderSearchLocation(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => "%USERPROFILE%\\.claude\\.credentials.json",
        "codex" => "%USERPROFILE%\\.codex\\auth.json",
        "antigravity" => "language_server.exe veya %USERPROFILE%\\.gemini\\antigravity-cli\\cli.log",
        "opencode" => "%USERPROFILE%\\.local\\share\\opencode\\auth.json veya opencode.db",
        _ => "yerel sağlayıcı kaynakları",
    };

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
        UpdateProviderLinks(_selectedId);

        if (!_scheduler.Current.TryGetValue(_selectedId, out var snapshot))
        {
            DetailName.Text = TabDisplayName(_selectedId);
            DetailUpdated.Text = "Bekleniyor…";
            DetailUpdated.Visibility = Visibility.Visible;
            DetailPlanBadge.Visibility = Visibility.Collapsed;
            DetailError.Visibility = Visibility.Collapsed;
            DetailUnavailable.Visibility = Visibility.Collapsed;
            SetWindows(null);
            ClearCostSection();
            return;
        }

        if (!string.Equals(_costForId, snapshot.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            ClearCostSection();
        }

        DetailName.Text = TabDisplayName(snapshot.ProviderId);
        QuotaVisuals.ApplyPlan(DetailPlanBadge, DetailPlanText, snapshot.PlanName);
        ToolTipService.SetToolTip(
            DetailPlanBadge,
            snapshot.ProviderId.Equals("opencode", StringComparison.OrdinalIgnoreCase) && snapshot.Windows.Count == 0
                ? "OpenCode'un kendi kotası yok, yapılandırdığın sağlayıcıların aboneliğini kullanır."
                : null);

        DetailUnavailableTitle.Visibility = Visibility.Visible;

        var stale = snapshot is { Status: ProviderStatus.Degraded, StaleReason: not null } &&
            (snapshot.Windows.Count > 0 || snapshot.Cost is not null || snapshot.Credits is not null);
        DetailUpdated.Text = stale ||
            snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error
            ? string.Empty
            : QuotaVisuals.FormatUpdated(snapshot.FetchedAt);
        // Boş satır yer kaplamasın: hata/bayat durumda başlık doğrudan ayırıcıya iner.
        DetailUpdated.Visibility = DetailUpdated.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (stale)
        {
            ShowStaleNotice(
                snapshot.FetchedAt,
                snapshot.ProviderId.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
                snapshot.StaleReason?.StartsWith("Oturum yenilenmeli", StringComparison.Ordinal) == true);
        }
        else
        {
            DetailError.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(DetailError, null);
        }

        SetFreeUsage(snapshot.Cost?.FreeUsage);
        var isOpenCode = snapshot.ProviderId.Equals("opencode", StringComparison.OrdinalIgnoreCase);

        if (snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error)
        {
            DetailUnavailable.Visibility = Visibility.Collapsed;
            DetailErrorTitle.Text = snapshot.Status == ProviderStatus.AuthRequired ? "Oturum Süresi Doldu" : "Kota Alınamadı";
            DetailErrorDetail.Text = snapshot.Status == ProviderStatus.AuthRequired
                ? UserErrorDetail(snapshot)
                : snapshot.StaleReason ?? "Şu an güncellenemiyor. Otomatik olarak yeniden denenecek.";
            DetailError.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(DetailError, null);
            SetWindows(null);
        }
        else if (isOpenCode && snapshot.Windows.Count == 0 &&
                 (snapshot.Cost is not null || !string.IsNullOrWhiteSpace(snapshot.StatusDetail)))
        {
            // OpenCode'un kendi kotası yok: yapılandırılmış sağlayıcılar + yerel kullanım.
            DetailUnavailableTitle.Visibility = Visibility.Collapsed;
            var noQuotaDetail = FormatOpenCodeNoQuota(snapshot);
            DetailUnavailableDetail.Text = noQuotaDetail;
            DetailUnavailable.Visibility = string.IsNullOrWhiteSpace(noQuotaDetail)
                ? Visibility.Collapsed
                : Visibility.Visible;
            SetWindows(null);
        }
        else if (snapshot.ProviderId.Equals("antigravity", StringComparison.OrdinalIgnoreCase) &&
                 snapshot.Windows.Count == 0 &&
                 snapshot.Cost is not null)
        {
            DetailUnavailable.Visibility = Visibility.Collapsed;
            SetWindows(null);
        }
        else if (snapshot.Status == ProviderStatus.NotInstalled)
        {
            DetailUnavailableTitle.Text = "Kurulu değil veya açık değil";
            DetailUnavailableDetail.Text = snapshot.StaleReason ?? "Antigravity açık değil.";
            DetailUnavailable.Visibility = Visibility.Visible;
            SetWindows(null);
        }
        else if (snapshot.Windows.Count == 0)
        {
            DetailUnavailableTitle.Text = "Veri yok";
            DetailUnavailableDetail.Text = "Sağlayıcıdan kullanılabilir kota alınamadı.";
            DetailUnavailable.Visibility = Visibility.Visible;
            SetWindows(null);
        }
        else
        {
            DetailUnavailable.Visibility = Visibility.Collapsed;
            SetWindows(snapshot);
        }
    }

    private void SetWindows(UsageSnapshot? snapshot)
    {
        var rows = snapshot is null ? Array.Empty<WindowRow>() : WindowRow.From(snapshot, DateTimeOffset.UtcNow);
        WindowList.ItemsSource = rows;
        WindowList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetFreeUsage(FreeModelUsage? freeUsage)
    {
        FreeUsageText.Text = freeUsage is null ? string.Empty : $"Ücretsiz modeller · bugün {freeUsage.RequestsToday} istek";
        FreeUsageSection.Visibility = freeUsage is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Sağlayıcının kendi kullanım ve durum sayfaları; bilinmeyende bağlantı gösterilmez.
    private static (string? Dashboard, string? Status) ProviderLinks(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => ("https://claude.ai/settings/usage", "https://status.anthropic.com"),
        "codex" => ("https://chatgpt.com/codex/settings/usage", "https://status.openai.com"),
        _ => (null, null),
    };

    private void UpdateProviderLinks(string providerId)
    {
        var (dashboard, status) = ProviderLinks(providerId);
        DashboardLink.NavigateUri = dashboard is null ? null : new Uri(dashboard);
        DashboardLink.Visibility = dashboard is null ? Visibility.Collapsed : Visibility.Visible;
        StatusLink.NavigateUri = status is null ? null : new Uri(status);
        StatusLink.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsLocalCostProvider(string providerId) =>
        providerId.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
        providerId.Equals("codex", StringComparison.OrdinalIgnoreCase);

    private string TabDisplayName(string providerId) =>
        _providers.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? providerId;

    private TabDef GetProviderTab(string providerId, string? fallbackName = null)
    {
        var name = _providers.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? fallbackName
            ?? providerId;

        return new TabDef(providerId, name, ProviderIcons.Get(providerId));
    }

    private static Microsoft.UI.Xaml.Media.Brush ProviderIconBrush(TabDef tab, bool active)
        => ProviderIcons.GetBrush(tab.Icon, active);

    private static UIElement CreateProviderIcon(TabDef tab, bool active = false)
        => ProviderIcons.Create(tab.Icon, 16, active);

    private static void SetProviderIconBrush(UIElement icon, Microsoft.UI.Xaml.Media.Brush brush)
        => ProviderIcons.SetBrush(icon, brush);

    private void SetMostUsedModel(string providerId, CostReport? report)
    {
        var models = ProviderModelLine.Summarize(report);
        LogModelSummary(providerId, models);
        ProviderModelLine.SetReport(ModelLine, report);
    }

    private void LogModelSummary(string providerId, CostReport? report) =>
        LogModelSummary(providerId, ProviderModelLine.Summarize(report));

    private void LogModelSummary(string providerId, IReadOnlyList<ModelTokenUsage> models)
    {
        var diagnostic = models.Count == 0
            ? "0 farklı model, en çok=yok %0"
            : $"{models.Count} farklı model, en çok={models[0].Model} %{models[0].Tokens * 100d / Math.Max(1, models.Sum(model => model.Tokens)):F0}";
        if (!_modelDiagnosticState.TryGetValue(providerId, out var previousDiagnostic) ||
            !string.Equals(previousDiagnostic, diagnostic, StringComparison.Ordinal))
        {
            _modelDiagnosticState[providerId] = diagnostic;
            Trace.Info("model", $"{providerId}: {diagnostic}");
        }
    }

    private void ShowStaleNotice(DateTimeOffset fetchedAt, bool sessionRenewalRequired)
    {
        DetailErrorTitle.Text = sessionRenewalRequired
            ? "Oturum yenilenmeli"
            : FormatStaleAge(fetchedAt);
        DetailErrorDetail.Text = string.Empty;
        DetailError.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(
            DetailError,
            "Şu an güncellenemiyor. Otomatik olarak yeniden denenecek.");
    }

    private static string FormatStaleAge(DateTimeOffset fetchedAt)
    {
        var age = DateTimeOffset.UtcNow - fetchedAt;
        if (age < TimeSpan.FromMinutes(1)) return "Az önceki veri";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} dk önceki veri";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} sa önceki veri";
        return $"{(int)age.TotalDays} gün önceki veri";
    }

    private string UserErrorDetail(UsageSnapshot snapshot)
    {
        if (snapshot.ProviderId.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
            snapshot.StaleReason?.StartsWith("Oturum yenilenmeli", StringComparison.Ordinal) == true)
        {
            return "Oturum yenilenmeli — Claude Code'u bir kez çalıştır";
        }

        return snapshot.ProviderId.Equals("claude", StringComparison.OrdinalIgnoreCase)
            ? "Claude Code'da tekrar giriş yapın."
            : "Sağlayıcıda tekrar giriş yapın.";
    }

    private static string FormatOpenCodeNoQuota(UsageSnapshot snapshot)
    {
        return snapshot.ConfiguredProviders is { Count: > 0 } providers
            ? string.Join(" · ", providers)
            : string.Empty;
    }

    // ---- Kullanım özeti: yerel JSONL taraması, thread pool'da; bitince seçiliyse yaz. ----

    private void RefreshCost(bool force)
    {
        var id = _selectedId;
        if (!IsCostProvider(id))
        {
            ClearCostSection();
            return;
        }

        if (!force && id.Equals(_costForId, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - _costAt < TimeSpan.FromMinutes(5))
        {
            return;
        }

        if (!IsLocalCostProvider(id))
        {
            // OpenCode/Antigravity raporu snapshot ile gelir; tarama gerekmez.
            _costForId = id;
            _costAt = DateTimeOffset.UtcNow;
            var localCost = _scheduler.Current.TryGetValue(id, out var snapshot)
                ? snapshot.Cost
                : null;
            if (localCost is null)
            {
                ClearCostSection();
                return;
            }

            var pricing = PricingTable.LoadOrEmpty();
            var aliases = id.Equals("antigravity", StringComparison.OrdinalIgnoreCase)
                ? ModelAliasTable.LoadOrEmpty()
                : null;
            var freeModels = id.Equals("opencode", StringComparison.OrdinalIgnoreCase)
                ? FreeModelCatalog.LoadOrEmpty()
                : null;
            ApplyCost(
                null,
                CostEstimator.Estimate(localCost, pricing, aliases, freeModels),
                pricing,
                freeModels);
            return;
        }

        var run = ++_costRun;
        _costForId = id;
        _costAt = DateTimeOffset.UtcNow;
        Task.Run(() =>
        {
            CostScanResult Scan(DateTimeOffset since) => id switch
            {
                "claude" => ClaudeCostScanner.Scan(since),
                _ => CodexCostScanner.Scan(since),
            };
            var pricing = PricingTable.LoadOrEmpty();
            return (
                Today: CostEstimator.Estimate(Scan(new DateTimeOffset(DateTime.Today)), pricing),
                Month: CostEstimator.Estimate(Scan(DateTimeOffset.UtcNow.AddDays(-30)), pricing),
                Pricing: pricing);
        }).ContinueWith(t =>
        {
            if (run != _costRun) return;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                // Sessiz düşme: tanı için türü yaz (token/içerik asla loglanmaz).
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

    // Grafik bölümü ilk çizimde gizli olabilir (ActualWidth=0); kaydırma alanının görünen
    // genişliği güvenilirdir. Henüz ölçülmediyse kaydırma çubuğu payıyla tahmin et.
    private double ChartWidth() =>
        DetailScrollViewer.ViewportWidth > 0 ? DetailScrollViewer.ViewportWidth : FlyoutWidthDip - 48;

    private static bool IsCostProvider(string providerId) =>
        IsLocalCostProvider(providerId) ||
        providerId.Equals("opencode", StringComparison.OrdinalIgnoreCase) ||
        providerId.Equals("antigravity", StringComparison.OrdinalIgnoreCase);

    private void ClearCostSection()
    {
        _costForId = null;
        _costAt = DateTimeOffset.MinValue;
        _costRun++;
        CostSection.Visibility = Visibility.Collapsed;
        StatList.ItemsSource = null;
        DailyChart.ItemsSource = null;
        ToolTipService.SetToolTip(CostInfoIcon, null);
        ModelLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// CodexBar düzeni: iki sütunlu istatistik ızgarası + 30 günlük çubuk grafik +
    /// en çok kullanılan model. Fiyat tablosu boşsa para GÖSTERİLMEZ (sıfır dolar
    /// yanlış bilgidir); yalnızca tokenlar yazılır.
    /// </summary>
    private void ApplyCost(
        CostReport? today,
        CostReport? month,
        PricingTable pricing,
        FreeModelCatalog? freeModels = null)
    {
        var reports = new[] { today, month }.OfType<CostReport>().Where(r => r.TotalTokens > 0).ToArray();
        var mostUsed = reports.OrderByDescending(r => r.TotalTokens).FirstOrDefault();
        SetMostUsedModel(_selectedId, mostUsed);

        if (reports.Length == 0)
        {
            CostSection.Visibility = Visibility.Collapsed;
            return;
        }

        string? Money(CostReport? report)
        {
            if (report is null || pricing.IsEmpty) return null;
            if (report.TotalTokens == 0) return UsageFormat.Money(0, pricing.Currency);
            var unknown = report.ModelsWithoutPricing ?? Array.Empty<string>();
            var models = report.Models ?? Array.Empty<ModelTokenUsage>();
            var allFree = freeModels is not null && models.Count > 0 && models.All(m => freeModels.IsFree(m.Model));
            var hasPriced = models.Any(m => !unknown.Contains(m.Model, StringComparer.OrdinalIgnoreCase));
            return allFree || !hasPriced
                ? null
                : UsageFormat.Money(report.TotalCost, pricing.Currency) + (unknown.Count > 0 ? "*" : string.Empty);
        }

        var monthLabel = month is { PeriodKnown: false } ? "Toplam" : "Son 30 gün";
        var stats = new List<StatItem>();
        var todayMoney = Money(today);
        var monthMoney = Money(month);

        if (todayMoney is not null || monthMoney is not null)
        {
            if (today is not null) stats.Add(new StatItem("Bugün", todayMoney ?? "—"));
            if (month is not null) stats.Add(new StatItem($"{monthLabel} maliyeti", monthMoney ?? "—"));
        }
        if (today is not null) stats.Add(new StatItem("Bugün token", UsageFormat.Tokens(today.TotalTokens)));
        if (month is not null) stats.Add(new StatItem($"{monthLabel} token", UsageFormat.Tokens(month.TotalTokens)));
        StatList.ItemsSource = stats;

        var daily = month?.Daily ?? Array.Empty<DailyTokens>();
        DailyChart.ItemsSource = daily.Count == 0
            ? null
            : UsageFormat.Bars(daily, DateOnly.FromDateTime(DateTime.Today), 30, ChartWidth(), 44);
        DailyChart.Visibility = daily.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var unpricedCount = pricing.IsEmpty
            ? 0
            : reports
                .SelectMany(r => r.ModelsWithoutPricing ?? Array.Empty<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        var tooltipLines = new List<string>
        {
            "Tokenlar yerel oturum loglarından okunur. Maliyet, aboneliğinle ödediğin tutar değil: aynı kullanım API fiyatlarıyla bu kadar tutardı.",
        };
        if (unpricedCount > 0) tooltipLines.Add($"{unpricedCount} model fiyat tablosunda yok, toplama dahil edilmedi.");
        if (pricing.DownloadedAt is { } downloadedAt) tooltipLines.Add($"Fiyatlar {downloadedAt.ToLocalTime():dd.MM.yyyy} itibarıyla.");
        ToolTipService.SetToolTip(CostInfoIcon, string.Join("\n", tooltipLines));

        CostSection.Visibility = Visibility.Visible;
        EnqueueResize();
    }

    public void UpdateTrayIcon(double? percent, string? tooltip = null)
    {
        bool isLightTheme = AppThemePreference.IsTaskbarLightTheme();

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        if (dpi == 0) dpi = 96;

        int iconSize = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        if (iconSize <= 0) iconSize = (int)Math.Round(16 * (dpi / 96.0));

        tooltip ??= _currentTooltip;

        IntPtr iconHandle = TrayIconRenderer.CreateGaugeIconHandle(percent, isLightTheme, iconSize);
        IntPtr previousHandle = _currentIconHandle;

        _currentIconHandle = iconHandle;

        // Once yeni ikon kabuga verilir, sonra onceki handle yok edilir.
        _tray.UpdateIcon(iconHandle);
        _tray.UpdateTooltip(tooltip);

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

    /// <summary>Screenshot doğrulamasında taşan detayın sonunu görünür kılar.</summary>
    public void ScrollDetailToEndForVerification()
    {
        RootLayout.UpdateLayout();
        DetailScrollViewer.UpdateLayout();
        DetailScrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
    }

    public void ShowFlyout()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        double scale = dpi / 96.0;

        int targetWidth = (int)Math.Round(FlyoutWidthDip * scale);
        _targetWidth = targetWidth;

        // Genişlik sabit kalır; yalnızca seçili sekmenin içeriği ölçülür.
        double desiredHeightDip = MeasureSelectedContentDip();
        if (desiredHeightDip <= 0) desiredHeightDip = 390;

        // Imlec konumuna dus: tiklamayla acarken zaten dogru sonucu verir.
        // GUID ile Shell_NotifyIconGetRect denemeye gerek yok.
        var (tempX, tempY) = FlyoutPositioner.CalculatePosition(
            Guid.Empty,
            _hwnd,
            0,
            targetWidth,
            (int)Math.Round(desiredHeightDip * scale));
        var pt = new NativeMethods.POINT { X = tempX, Y = tempY };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
        NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo);

        int maxHeight = (int)Math.Round(monitorInfo.rcWork.Height * 0.85);

        int targetHeight = Math.Min(
            (int)Math.Round((desiredHeightDip + 12) * scale),
            maxHeight);

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
        _edge = edge;
        _anchorBottom = y + targetHeight;
        PopoverHelper.ShowPopover(_appWindow, this, _hwnd, RootLayout, edge);

        _isVisible = true;
        Trace.Info("window", "flyout.show");

        _ = RefreshOnFlyoutShownAsync();
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

        var desiredHeightDip = MeasureSelectedContentDip();
        if (desiredHeightDip <= 0) return;

        var dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;

        int targetHeight = Math.Min(
            (int)Math.Round((desiredHeightDip + 12) * scale),
            PopoverHelper.WorkAreaMaxHeight());
        if (targetHeight <= 0) return;

        var position = _appWindow.Position;
        var pt = new NativeMethods.POINT { X = position.X, Y = position.Y };
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
        NativeMethods.GetMonitorInfo(
            NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST), ref monitorInfo);

        // Alt görev çubuğu: alt kenar sabit, pencere yukarı büyür. Üst/yan: üst kenar sabit,
        // alttan taşarsa yukarı itilir. Her durumda çalışma alanının içinde kalır.
        var y = _edge == FlyoutEdge.Bottom
            ? _anchorBottom - targetHeight
            : Math.Min(position.Y, monitorInfo.rcWork.Bottom - targetHeight);
        y = Math.Max(y, monitorInfo.rcWork.Top);

        PopoverHelper.ResizeWithAnimation(
            _appWindow,
            RootLayout,
            new RectInt32(position.X, y, _targetWidth, targetHeight),
            _appWindow.Size.Height,
            anchorBottom: _edge == FlyoutEdge.Bottom);
    }

    private double MeasureSelectedContentDip()
    {
        RootLayout.Measure(new Windows.Foundation.Size(FlyoutWidthDip, double.PositiveInfinity));
        return RootLayout.DesiredSize.Height;
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
            ? $"{value.Name} · %{100 - value.Percent:F0} kaldı"
            : _trayProviderId is { } missing
                ? $"{TabDisplayName(missing)}: Veri yok"
                : "Ration: Veri yok";

        _currentGaugePercent = gaugePercent;
        _currentTooltip = tooltip;
        UpdateTrayIcon(gaugePercent, tooltip);
    }
}
