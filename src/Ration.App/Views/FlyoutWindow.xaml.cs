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
using Ration.Core.Layout;
using Ration.Core.Model;
using Ration.Core.Providers;
using Ration.Core.Providers.Status;
using Ration.Core.Refresh;
using Ration.App.Updates;
using Ration.Core.Updates;
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
    private readonly UpdateAutoCheck _updateAutoCheck;
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
    private int _resumeRefreshPending;
    private bool _isVisible;
    private bool _pinned;
    private double? _currentGaugePercent;
    private bool _currentGaugeStale;
    private string _currentTooltip = L.T("Ration: No data", "Ration: Veri yok");
    private string? _trayProviderId = TrayProviderPreference.Current;
    private SettingsWindow? _settingsWindow;
    private IntPtr _currentIconHandle = IntPtr.Zero;
    private bool _selfTestExitInProgress;

    /// <summary>
    /// A hook that lets the self-test observe the exit action once and postpone it if needed.
    /// It stays null in normal operation; it does not change the menu behavior.
    /// </summary>
    public Func<Task<bool>>? SelfTestExitRequested { get; set; }

    // Tab strip state. The tray is untouched; the icon calculation stays as it is in RecalculateTrayIcon.
    private sealed record TabDef(string Id, string Name, ProviderIconDefinition Icon);
    private readonly Dictionary<string, (ToggleButton Button, ProgressBar Meter)> _tabs = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedId = "claude";
    private bool _userPickedTab;

    // Cost: local JSONL scan, on the thread pool. Stale runs are thrown away.
    private long _costRun;
    private string? _costForId;
    private DateTimeOffset _costAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, string> _modelDiagnosticState = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _modelDiagnosticSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private bool _allModelDiagnosticsStarted;

    // The flyout width is a fixed 420 DIP (so that five tabs, including long names like Antigravity,
    // fit without clipping); the height is that of the tallest of all tabs.
    private const double FlyoutWidthDip = 420;
    private int _targetWidth;

    // Placement on opening: when the taskbar is at the bottom, the bottom edge is kept fixed so that when
    // the content changes the window neither detaches from the taskbar nor overflows the screen.
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

        // Shared popover shell: acrylic, title-less presenter, hidden from switchers.
        PopoverHelper.ConfigureChrome(this, _appWindow, _hwnd);
        // While pinned, losing focus does not close the window and no timestamp is recorded: otherwise clicking
        // the tray icon (the click that takes focus) would be swallowed as a close.
        PopoverHelper.ConfigureDismissal(this, _appWindow, HideFlyout, RootLayout,
            onDeactivated: () => { if (!_pinned) _lastDeactivatedTime = DateTimeOffset.UtcNow; },
            canDismissOnDeactivate: () => !_pinned);

        // Immersive Dark Mode
        UpdateWindowFrameTheme();

        // Subclass hook: WM_DWMCOLORIZATIONCOLORCHANGED (0x0320) and WM_SETTINGCHANGE (0x001A)
        _subclassProc = WindowSubclassProc;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, new UIntPtr(1), IntPtr.Zero);

        // Light dismiss (focus loss/Esc/close) lives in the common base; the timestamp
        // prevents the same click from closing the window and immediately reopening it.

        // Initialize HTTP Client and RefreshScheduler
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Ration/{AppUpdates.CurrentVersion}");

        _providers = ProviderRegistry.CreateAll(
            _http,
            AntigravityProcessPortFinder.FindPorts,
            AntigravityProcessPortFinder.FindEndpoints,
            CursorTokenProtector.TryDecrypt);
        _selectedId = _providers.FirstOrDefault()?.Id ?? "claude";

        _scheduler = new RefreshScheduler(_providers, options: new RefreshOptions
        {
            Interval = RefreshIntervalPreference.Current,
            MaxJitter = TimeSpan.FromSeconds(5),
            // All providers at once: if the limit is lower than the number of providers, the last one waits in line
            // and stayed on old data when the panel opened.
            MaxConcurrency = _providers.Count,
            EmitCachedOnStart = true,
        });

        _scheduler.SnapshotUpdated += OnSnapshotUpdated;

        // Wire window refresh button
        RefreshButton.Click += (s, e) => _ = RefreshManuallyAsync();
        // Checked/Unchecked rather than Click: screen readers and UI Automation toggle a ToggleButton
        // without Click.
        PinButton.Checked += OnPinChanged;
        PinButton.Unchecked += OnPinChanged;

        SettingsButton.Click += (s, e) => OpenSettingsWindow();
        UsageDisplayPreference.Changed += () => DispatcherQueue.TryEnqueue(OnUsageDisplayChanged);
        TrayWindowPreference.Changed += () => DispatcherQueue.TryEnqueue(RecalculateTrayIcon);
        ProviderOutages.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            RenderOutage();
            EnqueueResize();
        });
        TabStrip.KeyDown += OnTabStripKeyDown;
        TabScroll.SizeChanged += (_, _) => UpdateTabStripWidth();
        TabScroll.PointerWheelChanged += OnTabScrollWheel;
        CopyErrorButton.Click += OnCopyErrorClicked;

        BuildTabs();
        UpdateWelcomeState();

        // The menu is a native WinUI window (TrayMenuWindow); there is no WinForms menu.
        // The menu is built first (the tray lambdas close over it).
        _menu = new TrayMenuWindow();
        _menu.RefreshRequested += () => _ = RefreshManuallyAsync();
        _menu.SettingsRequested += () => this.DispatcherQueue.TryEnqueue(OpenSettingsWindow);
        _menu.ExitRequested += () => this.DispatcherQueue.TryEnqueue(ExitApplication);

        // Tray: raw Shell_NotifyIcon (SystemTrayHost). The icon is produced as an HICON,
        // ownership passes to SystemTrayHost: first the new icon is given to the shell,
        // then the previous handle is destroyed (see UpdateTrayIcon).
        _tray = new SystemTrayHost();
        _tray.LeftClicked += () => this.DispatcherQueue.TryEnqueue(() =>
        {
            // Left click while the menu is open: the menu closes and the flyout opens (the two never stay together).
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

        // Clicking a quota notification opens the panel.
        QuotaNotifier.Register(() => this.DispatcherQueue.TryEnqueue(ShowFlyout));

        // Theme listeners
        WindowsThemeListener.ThemeChanged += OnTaskbarThemeChanged;
        AppThemePreference.Changed += OnAppThemeChanged;
        RefreshIntervalPreference.Changed += OnRefreshIntervalChanged;
        HotkeyPreference.Changed += () => this.DispatcherQueue.TryEnqueue(ApplyHotkey);
        ApplyHotkey();
        EfficiencyModeManager.PauseChanged += OnEfficiencyPauseChanged;
        EfficiencyModeManager.Resumed += OnSystemResumed;
        PricingTableUpdater.Updated += OnPricingUpdated;
        WindowsThemeListener.AccentChanged += OnAccentChanged;
        WindowsThemeListener.DisplayChanged += OnDisplayChanged;

        _appWindow.Resize(new SizeInt32(1, 1));

        // Starts hidden: low CPU priority (EcoQoS), but background refresh runs.
        // It starts paused only if screen lock / energy saver is on.
        if (EfficiencyModeManager.ShouldPause) _scheduler.Pause();
        EfficiencyModeManager.SetEfficiencyMode(true);

        // Start scheduler loop
        _scheduler.Start();

        // If there is a new version, a dot appears next to Settings in the tray menu.
        _menu.SetUpdateAvailable(AppUpdates.HasNewerUpdate(AppUpdates.Service.LastResult));
        AppUpdates.Service.ResultChanged += OnUpdateResultChanged;

        // Silent update check once a day; it waits while a window is open or the screen is locked.
        _updateAutoCheck = new UpdateAutoCheck(
            this.DispatcherQueue,
            AppUpdates.Service,
            () => _isVisible || _menu.IsMenuVisible || _settingsWindow?.IsShown == true || EfficiencyModeManager.ShouldPause);
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            this.DispatcherQueue.TryEnqueue(Toggle);
            return IntPtr.Zero;
        }

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

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        var pinned = PinButton.IsChecked == true;
        if (pinned == _pinned) return;

        _pinned = pinned;
        Trace.Info("window", $"flyout.pin state={(pinned ? "on" : "off")}");
    }

    // Global hotkey: RegisterHotKey sends WM_HOTKEY to this window (WindowSubclassProc).
    // When the setting changes, the old registration is removed and a new one is made; while the hotkey is off nothing stays registered.
    private const int HotkeyId = 0x5254;
    private bool _hotkeyRegistered;

    private void ApplyHotkey()
    {
        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyRegistered = false;
        }

        if (!HotkeyPreference.Enabled || HotkeyPreference.Capturing)
        {
            HotkeyPreference.ReportRegistration(failed: false);
            return;
        }

        var hotkey = HotkeyPreference.Current;
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            _hwnd, HotkeyId, (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)hotkey.VirtualKey);
        Trace.Info("hotkey", _hotkeyRegistered
            ? $"registered key={hotkey}"
            : $"register-failed key={hotkey} error={Marshal.GetLastWin32Error()}");

        // If another app holds the combination (ERROR_HOTKEY_ALREADY_REGISTERED) the settings warn.
        HotkeyPreference.ReportRegistration(failed: !_hotkeyRegistered);
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
            _settingsWindow.LanguageChanged += () => this.DispatcherQueue.TryEnqueue(() => RestartApp("language"));
            _settingsWindow.SettingsReset += () => this.DispatcherQueue.TryEnqueue(() => RestartApp("reset"));
            _settingsWindow.SettingsImported += () => this.DispatcherQueue.TryEnqueue(() => RestartApp("import"));
            _settingsWindow.UpdateRestartRequested += () => this.DispatcherQueue.TryEnqueue(() =>
            {
                Trace.Info("app", "exit for update");
                ExitApplicationCore();
            });
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
            _ = RefreshAfterResumeAsync();
        }

        Trace.Info("provider.refresh", $"polling={(shouldPause ? "paused" : "resumed")}");
    }

    private void OnSystemResumed() => _ = RefreshAfterResumeAsync();

    // After returning from sleep or lock, stale data must not remain until the next round (15 min).
    // The network adapter can be late to get ready, so a short wait is applied; if the resume from sleep
    // and the unlock arrive back to back, a single refresh is enough.
    private async Task RefreshAfterResumeAsync()
    {
        if (Interlocked.Exchange(ref _resumeRefreshPending, 1) == 1) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (EfficiencyModeManager.ShouldPause) return;

            Trace.Info("provider.refresh", "resume status=started");
            await _scheduler.RefreshAllAsync();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            Volatile.Write(ref _resumeRefreshPending, 0);
        }
    }

    private async Task RefreshManuallyAsync()
    {
        if (EfficiencyModeManager.ShouldPause)
        {
            Trace.Info("provider.refresh", "manual status=skipped reason=efficiency");
            return;
        }

        // Only against rapid repeated presses: the automatic refresh on panel opening does not block the Refresh
        // button (it used to silently not work for 60 s because of a shared timestamp).
        var now = DateTimeOffset.UtcNow;
        if (now - _lastManualRefresh < ManualRefreshMinimum)
        {
            Trace.Info("provider.refresh", "manual status=skipped reason=minimum-interval");
            return;
        }

        _lastManualRefresh = now;
        _lastInteractiveRefresh = now;
        RefreshButton.IsEnabled = false;
        BeginRefreshIndicator();
        try
        {
            Trace.Info("provider.refresh", "manual status=started");
            await _scheduler.RefreshAllAsync(bypassCircuitBreaker: true, skipJitter: true);
            Trace.Info("provider.refresh", "manual status=completed");
        }
        catch (OperationCanceledException)
        {
            // The scheduler is cancelled during Exit; so that a TaskCanceledException does not leak into the
            // async-void Click and trigger the XAML unhandled flow.
            Trace.Info("provider.refresh", "manual status=cancelled");
        }
        catch (ObjectDisposedException)
        {
            // At shutdown another manual round can race with the scheduler dispose;
            // that too is a controlled shutdown case, not a user action.
            Trace.Info("provider.refresh", "manual status=cancelled");
        }
        catch (Exception ex)
        {
            Trace.Error("provider.refresh", $"manual status=exception type={ex.GetType().Name}");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            EndRefreshIndicator();
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
        BeginRefreshIndicator();
        try
        {
            Trace.Info("provider.refresh", "flyout status=started");
            await _scheduler.RefreshAllAsync(skipJitter: true);
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
        finally
        {
            EndRefreshIndicator();
        }
    }

    // With overlapping refreshes (button + panel opening) the ring spins until the last one finishes.
    private int _refreshesInFlight;

    private void BeginRefreshIndicator()
    {
        if (++_refreshesInFlight != 1) return;

        RefreshIcon.Visibility = Visibility.Collapsed;
        RefreshRing.Visibility = Visibility.Visible;
        RefreshRing.IsActive = true;
    }

    private void EndRefreshIndicator()
    {
        if (--_refreshesInFlight > 0) return;

        _refreshesInFlight = 0;
        RefreshRing.IsActive = false;
        RefreshRing.Visibility = Visibility.Collapsed;
        RefreshIcon.Visibility = Visibility.Visible;
    }

    private void OnUsageDisplayChanged()
    {
        UpdateTabs();
        RenderDetail();
        RecalculateTrayIcon();
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
        EfficiencyModeManager.Resumed -= OnSystemResumed;
        PricingTableUpdater.Updated -= OnPricingUpdated;

        NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, new UIntPtr(1));

        AppUpdates.Service.ResultChanged -= OnUpdateResultChanged;
        _updateAutoCheck.Dispose();
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

    private void OnUpdateResultChanged(UpdateCheckResult result) =>
        this.DispatcherQueue.TryEnqueue(() => _menu.SetUpdateAvailable(AppUpdates.HasNewerUpdate(result)));

    private void OnDisplayChanged()
    {
        this.DispatcherQueue.TryEnqueue(() => UpdateTrayIcon(_currentGaugePercent, _currentTooltip));
    }

    private void OnTaskbarThemeChanged(bool isLightTheme)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            if (RestartIfAppThemeChanged()) return;
            // The taskbar theme can change independently of the app theme: icon only.
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
    /// Because the app theme can only be set at startup (App.RequestedTheme), when the effective
    /// theme changes the process restarts itself; if Settings is open it is reopened.
    /// Patching at runtime left code-supplied colors, such as the tab labels, in the old theme.
    /// </summary>
    private bool RestartIfAppThemeChanged()
    {
        if (AppThemePreference.IsAppLightTheme() == App.AppliedLightTheme) return false;
        return RestartApp("theme");
    }

    /// <summary>Theme and language are applied only at startup; when they change the process restarts itself.</summary>
    private bool RestartApp(string reason)
    {
        var reopenSettings = _settingsWindow?.IsShown == true;
        Trace.Info("app", $"restart reason={reason} settings={(reopenSettings ? "open" : "closed")}");
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
            // If it cannot be restarted, do not exit; the current theme stays until the user restarts manually.
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

    // ---- Tab strip: ToggleButton + a 2px mini meter underneath. SelectorBar was not used because it cannot
    // host the mini meter; the provider count is small (2-6), so a StackPanel is enough. ----

    /// <summary>More providers than this scroll sideways instead of squeezing every name.</summary>
    private const int MaxVisibleTabs = 5;

    // The strip is as wide as the visible area, or as wide as needed to keep every tab at one fifth of it.
    private void UpdateTabStripWidth()
    {
        var viewport = TabScroll.ActualWidth;
        if (viewport <= 0 || _tabs.Count == 0) return;

        TabStrip.Width = viewport / Math.Min(_tabs.Count, MaxVisibleTabs) * _tabs.Count;

        // The scroll area learns its new extent in the layout pass that follows; scrolling and reading the extent
        // right now would still see the old one.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            BringSelectedTabIntoView();
        });
    }

    // A selected tab can sit outside the visible five (picked by a click, by the arrow keys or on its own).
    private void BringSelectedTabIntoView()
    {
        if (_tabs.TryGetValue(_selectedId, out var tab)) tab.Button.StartBringIntoView();
    }

    // The mouse wheel only moves up and down, so over the strip it scrolls sideways instead.
    private void OnTabScrollWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_tabs.Count <= MaxVisibleTabs) return;

        var delta = e.GetCurrentPoint(TabScroll).Properties.MouseWheelDelta;
        TabScroll.ChangeView(TabScroll.HorizontalOffset - delta, null, null);
        e.Handled = true;
    }

    private void BuildTabs()
    {
        TabStrip.Children.Clear();
        TabStrip.ColumnDefinitions.Clear();
        _tabs.Clear();

        foreach (var provider in _providers) EnsureTab(provider.Id, provider.DisplayName);
        UpdateTabStripWidth();
        UpdateTabs();
    }

    private void EnsureTab(string id, string? name = null)
    {
        if (_tabs.ContainsKey(id)) return;

        var known = GetProviderTab(id, name);

        // Equal-width tabs: icon on top, name below, the mini meter at the very bottom. The side-by-side
        // (icon + name) layout did not fit an equal width ("Antigravity"); with content-based width
        // the strip looked scattered.
        var icon = CreateProviderIcon(known);
        if (icon is FrameworkElement iconElement) iconElement.HorizontalAlignment = HorizontalAlignment.Center;

        var nameText = new TextBlock
        {
            Text = known.Name,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        QuotaVisuals.SetTextStyle(nameText, "CaptionTextBlockStyle");

        var meter = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = QuotaVisuals.Fill("SubtleFillColorTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(6, 2, 6, 0),
        };

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(icon);
        content.Children.Add(nameText);
        content.Children.Add(meter);

        var button = new ToggleButton
        {
            Content = content,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 6, 4, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = QuotaVisuals.PillCorner(),
        };
        ToolTipService.SetToolTip(button, known.Name);
        var capturedId = id;
        button.Click += (s, e) => SelectProvider(capturedId);

        // The default ToggleButton checked style applies the accent; the design wants thin gray.
        // Overriding the theme keys in the element's resource dictionary is shorter than writing a template.
        button.Resources["ToggleButtonBackgroundChecked"] = QuotaVisuals.Fill("SubtleFillColorSecondaryBrush");
        button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = QuotaVisuals.Fill("SubtleFillColorTertiaryBrush");
        button.Resources["ToggleButtonBackgroundCheckedPressed"] = QuotaVisuals.Fill("SubtleFillColorTertiaryBrush");
        var primaryText = QuotaVisuals.Fill("TextFillColorPrimaryBrush");
        button.Resources["ToggleButtonForegroundChecked"] = primaryText;
        button.Resources["ToggleButtonForegroundCheckedPointerOver"] = primaryText;
        button.Resources["ToggleButtonForegroundCheckedPressed"] = primaryText;

        _tabs[id] = (button, meter);
        TabStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(button, TabStrip.ColumnDefinitions.Count - 1);
        TabStrip.Children.Add(button);
    }

    // Left/Right arrows switch the tab and move focus to the new tab; they wrap around at the ends.
    private void OnTabStripKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch { VirtualKey.Left => -1, VirtualKey.Right => 1, _ => 0 };
        if (step == 0 || _tabs.Count < 2) return;

        var ids = _tabs.Keys.ToList();
        var index = ids.FindIndex(id => id.Equals(_selectedId, StringComparison.OrdinalIgnoreCase));
        var next = ids[(index + step + ids.Count) % ids.Count];
        SelectProvider(next);
        _tabs[next].Button.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void SelectProvider(string id)
    {
        _userPickedTab = true;
        _selectedId = id;
        UpdateTabs();
        BringSelectedTabIntoView();
        RenderDetail();
        PopoverHelper.FadeIn(DetailPanel);
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
                && content.Children.FirstOrDefault() is UIElement icon)
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
                // Error: a solid line in the warning color; so it can be told apart from the gray line of "no quota".
                meter.Value = 100;
                meter.Foreground = QuotaVisuals.Fill("SystemFillColorCautionBrush");
                continue;
            }

            if (snapshot.Status == ProviderStatus.NotInstalled || snapshot.Windows.Count == 0)
            {
                meter.Value = 0;
                meter.Foreground = tabBrush;
                AutomationProperties.SetName(button, L.T($"{snapshot.ProviderId}, no data", $"{snapshot.ProviderId}, veri yok"));
                continue;
            }

            var percent = TrayWindowSelector.Pick(snapshot, TrayWindowChoice.Highest)?.Percent ?? 0;
            var shown = UsageDisplayPreference.ShowUsed ? percent : 100 - percent;
            meter.Value = shown;
            meter.Foreground = tabBrush;
            AutomationProperties.SetName(button, UsageDisplayPreference.ShowUsed
                ? L.T($"{snapshot.ProviderId}, {shown:F0} percent used", $"{snapshot.ProviderId}, yüzde {shown:F0} kullanıldı")
                : L.T($"{snapshot.ProviderId}, {shown:F0} percent left", $"{snapshot.ProviderId}, yüzde {shown:F0} kaldı"));
        }
    }

    // ---- Single provider detail ----

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

        // A failed refresh is when an outage explains something; the check is cached for five minutes.
        if (snapshot.Status == ProviderStatus.Error || snapshot.IsStale) _ = ProviderOutages.RefreshAsync(_providers.Select(p => p.Id));
    }

    private void MaybeScheduleAllModelDiagnostics()
    {
        // Two full scans only for the diagnostic log; not at startup but when the panel is first opened.
        if (_allModelDiagnosticsStarted || !_isVisible || _modelDiagnosticSnapshots.Count < 4) return;

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
        TabScroll.Visibility = discovered ? Visibility.Visible : Visibility.Collapsed;
        TabDivider.Visibility = TabScroll.Visibility;

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
        "antigravity" => L.T("language_server.exe or %USERPROFILE%\\.gemini\\antigravity-cli\\cli.log", "language_server.exe veya %USERPROFILE%\\.gemini\\antigravity-cli\\cli.log"),
        "opencode" => L.T("%USERPROFILE%\\.local\\share\\opencode\\auth.json or opencode.db", "%USERPROFILE%\\.local\\share\\opencode\\auth.json veya opencode.db"),
        "cursor" => "%APPDATA%\\Cursor\\User\\globalStorage\\state.vscdb",
        "copilot" => "%USERPROFILE%\\.copilot\\session-state",
        _ => L.T("local provider sources", "yerel sağlayıcı kaynakları"),
    };

    /// <summary>
    /// If the user has not selected a tab yet, show the first provider that has data.
    /// (E.g. if Claude is getting 429 while Codex is full, an error page is not shown at startup.)
    /// Once the user has clicked, the selection is theirs and we never touch it again.
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
            BringSelectedTabIntoView();
        }
    }

    private void RenderOutage()
    {
        if (ProviderOutages.For(_selectedId) is not { Level: not OutageLevel.None } report)
        {
            DetailOutage.IsOpen = false;
            return;
        }

        var outage = report.Level == OutageLevel.Outage;
        DetailOutage.Severity = outage ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        DetailOutage.Title = outage ? L.T("Service outage", "Hizmet kesintisi") : L.T("Service issue", "Hizmet sorunu");
        DetailOutage.Message = report.Detail ?? L.T("See the provider's status page.", "Sağlayıcının durum sayfasına bak.");
        DetailOutage.IsOpen = true;
    }

    private void RenderDetail()
    {
        UpdateProviderLinks(_selectedId);
        RenderOutage();

        if (!_scheduler.Current.TryGetValue(_selectedId, out var snapshot))
        {
            DetailName.Text = TabDisplayName(_selectedId);
            DetailUpdated.Text = L.T("Waiting…", "Bekleniyor…");
            DetailUpdated.Visibility = Visibility.Visible;
            DetailPlanBadge.Visibility = Visibility.Collapsed;
            DetailError.IsOpen = false;
            DetailUnavailable.Visibility = Visibility.Collapsed;
            DetailResetCredits.Visibility = Visibility.Collapsed;
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
                ? L.T("OpenCode has no quota of its own; it uses the subscriptions of the providers you configured.", "OpenCode'un kendi kotası yok, yapılandırdığın sağlayıcıların aboneliğini kullanır.")
                : null);

        DetailUnavailableTitle.Visibility = Visibility.Visible;

        var stale = snapshot.IsStale &&
            (snapshot.Windows.Count > 0 || snapshot.Cost is not null || snapshot.Credits is not null);
        DetailUpdated.Text = stale ||
            snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error
            ? string.Empty
            : QuotaVisuals.FormatUpdated(snapshot.FetchedAt);
        // An empty row must not take up space: in error/stale state the heading goes directly down to the divider.
        DetailUpdated.Visibility = DetailUpdated.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (stale)
        {
            ShowStaleNotice(
                snapshot.FetchedAt,
                snapshot.ProviderId.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
                snapshot.StaleReason?.StartsWith(L.T("Session needs renewal", "Oturum yenilenmeli"), StringComparison.Ordinal) == true);
        }
        else
        {
            DetailError.IsOpen = false;
        }

        SetFreeUsage(snapshot.Cost?.FreeUsage);
        var isOpenCode = snapshot.ProviderId.Equals("opencode", StringComparison.OrdinalIgnoreCase);

        if (snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error)
        {
            DetailUnavailable.Visibility = Visibility.Collapsed;
            ShowErrorBar(
                snapshot.Status == ProviderStatus.AuthRequired ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
                snapshot.Status == ProviderStatus.AuthRequired ? L.T("Session expired", "Oturum Süresi Doldu") : L.T("Couldn't get quota", "Kota Alınamadı"),
                snapshot.Status == ProviderStatus.AuthRequired
                    ? UserErrorDetail(snapshot)
                    : snapshot.StaleReason ?? L.T("Can't update right now. Will retry automatically.", "Şu an güncellenemiyor. Otomatik olarak yeniden denenecek."));
            SetWindows(null);
        }
        else if (isOpenCode && snapshot.Windows.Count == 0 &&
                 (snapshot.Cost is not null || !string.IsNullOrWhiteSpace(snapshot.StatusDetail)))
        {
            // OpenCode has no quota of its own: configured providers + local usage.
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
        else if (snapshot.ProviderId.Equals("copilot", StringComparison.OrdinalIgnoreCase) &&
                 snapshot.Windows.Count == 0 &&
                 snapshot.Status == ProviderStatus.Ok)
        {
            // Copilot is read from local session logs and has no quota here; the Usage section below carries it.
            DetailUnavailableTitle.Visibility = Visibility.Collapsed;
            DetailUnavailableDetail.Text = snapshot.StatusDetail ?? string.Empty;
            DetailUnavailable.Visibility = string.IsNullOrWhiteSpace(DetailUnavailableDetail.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            SetWindows(null);
        }
        else if (snapshot.Status == ProviderStatus.NotInstalled)
        {
            DetailUnavailableTitle.Text = L.T("Not installed or not running", "Kurulu değil veya açık değil");
            DetailUnavailableDetail.Text = snapshot.StaleReason ?? L.T("Antigravity is not running.", "Antigravity açık değil.");
            DetailUnavailable.Visibility = Visibility.Visible;
            SetWindows(null);
        }
        else if (snapshot.Windows.Count == 0)
        {
            DetailUnavailableTitle.Text = L.T("No data", "Veri yok");
            DetailUnavailableDetail.Text = snapshot.StaleReason
                ?? L.T("No usable quota received from the provider.", "Sağlayıcıdan kullanılabilir kota alınamadı.");
            DetailUnavailable.Visibility = Visibility.Visible;
            SetWindows(null);
        }
        else
        {
            DetailUnavailable.Visibility = Visibility.Collapsed;
            SetWindows(snapshot);
        }

        RenderResetCredits(snapshot);
    }

    private void RenderResetCredits(UsageSnapshot snapshot)
    {
        var show = snapshot.Status is not (ProviderStatus.AuthRequired or ProviderStatus.Error) &&
                   snapshot.ResetCredits is { Available: > 0 };
        DetailResetCredits.Text = show ? ResetCreditAlerts.Describe(snapshot.ResetCredits!, DateTimeOffset.UtcNow) : string.Empty;
        DetailResetCredits.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetWindows(UsageSnapshot? snapshot)
    {
        var rows = snapshot is null ? Array.Empty<WindowRow>() : WindowRow.From(snapshot, DateTimeOffset.UtcNow, UsageDisplayPreference.ShowUsed);
        WindowList.ItemsSource = rows;
        WindowList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetFreeUsage(FreeModelUsage? freeUsage)
    {
        FreeUsageText.Text = freeUsage is null ? string.Empty : L.T($"Free models · {freeUsage.RequestsToday} requests today", $"Ücretsiz modeller · bugün {freeUsage.RequestsToday} istek");
        FreeUsageSection.Visibility = freeUsage is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // The provider's own usage and status pages; no link is shown for an unknown one.
    private static (string? Dashboard, string? Status) ProviderLinks(string providerId) => providerId.ToLowerInvariant() switch
    {
        "claude" => ("https://claude.ai/settings/usage", "https://status.anthropic.com"),
        "codex" => ("https://chatgpt.com/codex/settings/usage", "https://status.openai.com"),
        "cursor" => ("https://cursor.com/dashboard", "https://status.cursor.com"),
        "copilot" => ("https://github.com/settings/copilot", "https://www.githubstatus.com"),
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
        providerId.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
        providerId.Equals("copilot", StringComparison.OrdinalIgnoreCase);

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
            ? "0 models, top=none 0%"
            : $"{models.Count} models, top={models[0].Model} {models[0].Tokens * 100d / Math.Max(1, models.Sum(model => model.Tokens)):F0}%";
        if (!_modelDiagnosticState.TryGetValue(providerId, out var previousDiagnostic) ||
            !string.Equals(previousDiagnostic, diagnostic, StringComparison.Ordinal))
        {
            _modelDiagnosticState[providerId] = diagnostic;
            Trace.Info("model", $"{providerId}: {diagnostic}");
        }
    }

    private void ShowStaleNotice(DateTimeOffset fetchedAt, bool sessionRenewalRequired)
    {
        ShowErrorBar(
            InfoBarSeverity.Warning,
            sessionRenewalRequired
                ? L.T("Session needs renewal", "Oturum yenilenmeli")
                : FormatStaleAge(fetchedAt),
            L.T("Can't update right now. Will retry automatically.", "Şu an güncellenemiyor. Otomatik olarak yeniden denenecek."),
            copyable: false);
    }

    // An old-data notice has nothing worth copying; only a real error carries the Copy button.
    private void ShowErrorBar(InfoBarSeverity severity, string title, string message, bool copyable = true)
    {
        DetailError.Severity = severity;
        DetailError.Title = title;
        DetailError.Message = message;
        CopyErrorButton.Visibility = copyable ? Visibility.Visible : Visibility.Collapsed;
        DetailError.IsOpen = true;
    }

    private async void OnCopyErrorClicked(object sender, RoutedEventArgs e)
    {
        var copied = ClipboardHelper.TryCopy($"{DetailName.Text}: {DetailError.Title}. {DetailError.Message}");
        CopyErrorButton.Content = copied ? L.T("Copied", "Kopyalandı") : L.T("Could not copy", "Kopyalanamadı");
        await Task.Delay(TimeSpan.FromSeconds(2));
        CopyErrorButton.Content = L.T("Copy", "Kopyala");
    }

    private static string FormatStaleAge(DateTimeOffset fetchedAt)
    {
        var age = DateTimeOffset.UtcNow - fetchedAt;
        if (age < TimeSpan.FromMinutes(1)) return L.T("Data from just now", "Az önceki veri");
        if (age.TotalHours < 1) return L.T($"Data from {(int)age.TotalMinutes} min ago", $"{(int)age.TotalMinutes} dk önceki veri");
        if (age.TotalDays < 1) return L.T($"Data from {(int)age.TotalHours} h ago", $"{(int)age.TotalHours} sa önceki veri");
        return L.T($"Data from {(int)age.TotalDays} d ago", $"{(int)age.TotalDays} gün önceki veri");
    }

    private string UserErrorDetail(UsageSnapshot snapshot)
    {
        if (snapshot.ProviderId.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
            snapshot.StaleReason?.StartsWith(L.T("Session needs renewal", "Oturum yenilenmeli"), StringComparison.Ordinal) == true)
        {
            return L.T("Session needs renewal — run Claude Code once", "Oturum yenilenmeli — Claude Code'u bir kez çalıştır");
        }

        return snapshot.ProviderId.ToLowerInvariant() switch
        {
            "claude" => L.T("Sign in to Claude Code again.", "Claude Code'da tekrar giriş yapın."),
            "codex" => L.T("Run codex login in a terminal to sign in again.", "Yeniden giriş için bir terminalde codex login çalıştırın."),
            "opencode" => L.T("Run opencode auth login in a terminal to sign in again.", "Yeniden giriş için bir terminalde opencode auth login çalıştırın."),
            "cursor" => L.T("Open Cursor and sign in again.", "Cursor'ı açıp tekrar giriş yapın."),
            _ => L.T("Sign in to the provider again.", "Sağlayıcıda tekrar giriş yapın."),
        };
    }

    private static string FormatOpenCodeNoQuota(UsageSnapshot snapshot)
    {
        return snapshot.ConfiguredProviders is { Count: > 0 } providers
            ? string.Join(" · ", providers)
            : string.Empty;
    }

    // ---- Usage summary: local JSONL scan, on the thread pool; when done, write it if still selected. ----

    private void RefreshCost(bool force)
    {
        // No scan for a hidden panel; it is done when the panel opens (ShowFlyout).
        if (!_isVisible) return;

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
            // The OpenCode/Antigravity report comes with the snapshot; no scan is needed.
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
            // Single scan: "today" is derived from the day buckets of the 30-day scan (the same files
            // used to be read twice).
            var since = DateTimeOffset.UtcNow.AddDays(-30);
            var month = id switch
            {
                "claude" => ClaudeCostScanner.Scan(since),
                "copilot" => CopilotCostScanner.Scan(since),
                _ => CodexCostScanner.Scan(since),
            };
            var todayStart = new DateTimeOffset(DateTime.Today);
            var today = new CostScanResult(
                month.Tally.SinceDay(DateOnly.FromDateTime(DateTime.Today)), todayStart, month.PeriodEnd, month.FilesScanned);
            var pricing = PricingTable.LoadOrEmpty();
            return (
                Today: CostEstimator.Estimate(today, pricing),
                Month: CostEstimator.Estimate(month, pricing),
                Pricing: pricing);
        }).ContinueWith(t =>
        {
            if (run != _costRun) return;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                // Silent drop: write the type for diagnostics (tokens/content are never logged).
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

    // The chart section may be hidden at first paint (ActualWidth=0); the visible width of the scroll
    // area is reliable. If it has not been measured yet, estimate with the scrollbar allowance.
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
    /// CodexBar layout: a two-column statistics grid + a 30-day bar chart +
    /// the most used model. If the price table is empty, money is NOT shown (zero dollars
    /// is wrong information); only tokens are written.
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
                : "~" + UsageFormat.Money(report.TotalCost, pricing.Currency) + (unknown.Count > 0 ? "*" : string.Empty);
        }

        var monthLabel = month is { PeriodKnown: false } ? L.T("Total", "Toplam") : L.T("Last 30 days", "Son 30 gün");
        var stats = new List<StatItem>();
        var todayMoney = Money(today);
        var monthMoney = Money(month);

        // If the period cannot be priced (unknown model) the cost boxes are not shown at all; "$0.00" and "—"
        // side by side looked inconsistent. Only tokens remain.
        if (monthMoney is not null)
        {
            if (today is not null) stats.Add(new StatItem(L.T("Today", "Bugün"), todayMoney ?? "—"));
            stats.Add(new StatItem(L.T($"{monthLabel} cost", $"{monthLabel} maliyeti"), monthMoney));
        }
        if (today is not null) stats.Add(new StatItem(L.T("Today tokens", "Bugün token"), UsageFormat.Tokens(today.TotalTokens)));
        if (month is not null) stats.Add(new StatItem(L.T($"{monthLabel} tokens", $"{monthLabel} token"), UsageFormat.Tokens(month.TotalTokens)));
        StatList.ItemsSource = stats;

        var daily = month?.Daily ?? Array.Empty<DailyTokens>();
        DailyChart.ItemsSource = daily.Count == 0
            ? null
            : UsageFormat.Bars(daily, DateOnly.FromDateTime(DateTime.Today), 30, ChartWidth(), 44);
        DailyChart.Visibility = daily.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DailyChartAxis.Visibility = DailyChart.Visibility;

        var unpricedCount = pricing.IsEmpty
            ? 0
            : reports
                .SelectMany(r => r.ModelsWithoutPricing ?? Array.Empty<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        var tooltipLines = new List<string>
        {
            L.T("Tokens are read from local session logs. Cost is an estimate: not what your subscription charges, but what the same usage would cost at API prices.", "Tokenlar yerel oturum loglarından okunur. Maliyet bir tahmindir: aboneliğinle ödediğin tutar değil, aynı kullanım API fiyatlarıyla bu kadar tutardı."),
        };
        if (_selectedId.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            tooltipLines.Add(L.T("Cloud (ChatGPT) sessions of the Codex desktop app write no local logs and are not counted here; the quota bars include them.", "Codex masaüstü uygulamasının bulut (ChatGPT) oturumları yerel günlük yazmaz, burada sayılmaz; kota çubukları onları içerir."));
        }
        if (unpricedCount > 0) tooltipLines.Add(L.T($"{unpricedCount} model(s) missing from the pricing table, not included in the total.", $"{unpricedCount} model fiyat tablosunda yok, toplama dahil edilmedi."));
        if (pricing.DownloadedAt is { } downloadedAt) tooltipLines.Add(L.T($"Prices as of {downloadedAt.ToLocalTime():d}.", $"Fiyatlar {downloadedAt.ToLocalTime():dd.MM.yyyy} itibarıyla."));
        ToolTipService.SetToolTip(CostInfoIcon, string.Join("\n", tooltipLines));

        CostSection.Visibility = Visibility.Visible;
        EnqueueResize();
    }

    public void UpdateTrayIcon(double? percent, string? tooltip = null, bool? stale = null)
    {
        bool isLightTheme = AppThemePreference.IsTaskbarLightTheme();

        // The icon is drawn at the scale of the screen where the taskbar is; if the window's DPI came from
        // another screen, the icon would be produced at the wrong size and rescaled by the shell.
        uint dpi = _tray.TryGetBounds(out var trayBounds) && FlyoutPositioner.ForRect(trayBounds) is { } trayAnchor
            ? trayAnchor.Dpi
            : WindowDpi();

        int iconSize = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        if (iconSize <= 0) iconSize = (int)Math.Round(16 * (dpi / 96.0));

        tooltip ??= _currentTooltip;
        stale ??= _currentGaugeStale;

        IntPtr iconHandle = TrayIconRenderer.CreateGaugeIconHandle(percent, isLightTheme, iconSize, stale.Value);
        IntPtr previousHandle = _currentIconHandle;

        _currentIconHandle = iconHandle;

        // First give the new icon to the shell, then destroy the previous handle.
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
            // When the flyout opens the menu closes (the two never stay together).
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

    /// <summary>Verification hook (--menu): opens the tray menu without providers.</summary>
    public void ShowMenuForVerification() => _menu.ShowAtCursor();
    public TrayMenuWindow MenuWindow => _menu;
    public SettingsWindow? SettingsWindowForSelfTest => _settingsWindow;

    /// <summary>Makes the end of an overflowing detail visible in screenshot verification.</summary>
    public void ScrollDetailToEndForVerification()
    {
        RootLayout.UpdateLayout();
        DetailScrollViewer.UpdateLayout();
        DetailScrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
    }

    public void ShowFlyout()
    {
        // The DPI is taken from the target screen: the hidden window may currently sit on another screen with a different scale.
        // The tray icon's own rectangle comes first, so the panel sits in the corner by the taskbar however it was
        // opened (a click, the shortcut, a notification, a second launch). The mouse cursor is only the fallback.
        var anchor = (_tray.TryGetBounds(out var trayBounds) ? FlyoutPositioner.ForRect(trayBounds) : null)
            ?? FlyoutPositioner.ResolveAnchor(Guid.Empty, _hwnd, 0);
        uint dpi = anchor?.Dpi ?? WindowDpi();
        double scale = dpi / 96.0;

        int targetWidth = (int)Math.Round(FlyoutWidthDip * scale);
        _targetWidth = targetWidth;

        // The width stays fixed; only the content of the selected tab is measured.
        double desiredHeightDip = MeasureSelectedContentDip();
        if (desiredHeightDip <= 0) desiredHeightDip = 390;

        int maxHeight = anchor is { } known ? (int)Math.Round(known.Work.Height * 0.85) : int.MaxValue;
        int targetHeight = Math.Min((int)Math.Round((desiredHeightDip + 12) * scale), maxHeight);

        // Align from the cursor position, clamp to the work area.
        var placement = anchor?.Place(targetWidth, targetHeight) ?? new FlyoutPlacement(100, 100, FlyoutEdge.Bottom);
        var bounds = new RectInt32(placement.X, placement.Y, targetWidth, targetHeight);

        EfficiencyModeManager.SetEfficiencyMode(false);
        _appWindow.MoveAndResize(bounds);
        // When the window is moved to a screen with a different scale, Windows may apply its own suggested size;
        // the physical size we calculated is put back.
        if (NativeMethods.GetDpiForWindow(_hwnd) != dpi) _appWindow.MoveAndResize(bounds);
        _edge = placement.Edge;
        _anchorBottom = placement.Y + targetHeight;
        PopoverHelper.ShowPopover(_appWindow, this, _hwnd, RootLayout, placement.Edge);

        // Arrow keys are listened to on the tab strip; if focus does not land on the selected tab, you have to
        // press Tab first. Programmatic: when opened with the mouse no focus ring is drawn, it appears when
        // an arrow key is pressed.
        if (_tabs.TryGetValue(_selectedId, out var selectedTab)) selectedTab.Button.Focus(FocusState.Programmatic);

        _isVisible = true;
        Trace.Info("window", $"flyout.show dpi={dpi}");

        _ = RefreshOnFlyoutShownAsync();
        _ = ProviderOutages.RefreshAsync(_providers.Select(p => p.Id));
        RefreshCost(force: false);
    }

    private uint WindowDpi()
    {
        var dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        return dpi == 0 ? 96 : dpi;
    }

    /// <summary>
    /// Readjust the height as the content (tab, data) changes.
    /// If the measured height exceeds the 70% ceiling the window does not grow, the detail ScrollViewer scrolls.
    /// The tab strip and the action row are outside the scroll area and always visible.
    /// Layout is done in the next low-priority round: without measuring the added children,
    /// DesiredSize lags one frame behind and the window stays short.
    /// </summary>
    private void EnqueueResize() =>
        this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ResizeToContent);

    private void ResizeToContent()
    {
        if (!_isVisible || _targetWidth <= 0) return;

        var desiredHeightDip = MeasureSelectedContentDip();
        if (desiredHeightDip <= 0) return;

        var scale = WindowDpi() / 96.0;

        // The ceiling is the work area of the screen the window is on, not the cursor's.
        var position = _appWindow.Position;
        var pt = new NativeMethods.POINT { X = position.X, Y = position.Y };
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
        NativeMethods.GetMonitorInfo(
            NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST), ref monitorInfo);

        int targetHeight = Math.Min(
            (int)Math.Round((desiredHeightDip + 12) * scale),
            (int)Math.Round(monitorInfo.rcWork.Height * 0.85));
        if (targetHeight <= 0) return;

        // Bottom taskbar: the bottom edge is fixed, the window grows upward. Top/side: the top edge is fixed;
        // if it overflows at the bottom it is pushed up. In every case it stays inside the work area.
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

        // Flag first, then Hide: Hide can trigger a new Deactivated and re-enter.
        // Pinning is per session: the next opening is a light-dismiss flyout again.
        _isVisible = false;
        _pinned = false;
        PinButton.IsChecked = false;
        PopoverHelper.HidePopover(_appWindow, RootLayout);
        EfficiencyModeManager.SetEfficiencyMode(true);
        Trace.Info("window", "flyout.hide");
    }

    /// <summary>"Claude · Session · 12% left", or "· 88% used" when the percentages show usage; plain without a window name.</summary>
    private static string TooltipLine(string name, string? windowName, double usedPercent, bool stale)
    {
        var title = windowName is null ? name : $"{name} · {windowName}";
        var note = stale ? L.T(" (old data)", " (eski veri)") : "";
        return UsageDisplayPreference.ShowUsed
            ? L.T($"{title} · {usedPercent:F0}% used{note}", $"{title} · %{usedPercent:F0} kullanıldı{note}")
            : L.T($"{title} · {100 - usedPercent:F0}% left{note}", $"{title} · %{100 - usedPercent:F0} kaldı{note}");
    }

    private void RecalculateTrayIcon()
    {
        var available = new Dictionary<string, (string Name, double Percent, bool Stale, string WindowName)>(StringComparer.OrdinalIgnoreCase);
        var choice = TrayWindowPreference.Current;

        foreach (var provider in _providers)
        {
            var id = provider.Id;
            var name = provider.DisplayName;
            if (!_scheduler.Current.TryGetValue(id, out var snapshot)) continue;

            if (snapshot is not { Status: ProviderStatus.Ok or ProviderStatus.Degraded } || snapshot.Windows.Count == 0) continue;

            // 3. WHICH NUMBER TO SHOW IN THE TRAY ICON: the window picked by the user's choice
            // (the most used one by default). Per-model additional limits are left out (TrayWindowSelector).
            if (TrayWindowSelector.Pick(snapshot, choice) is { } window)
            {
                available[id] = (name, Math.Clamp(window.Percent, 0, 100), snapshot.IsStale, WindowRow.TitleFor(window));
            }
        }

        (string Name, double Percent, bool Stale, string WindowName)? selected = null;
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
        // The provider the icon shows is the first row; the others are sorted starting with the least remaining quota.
        string tooltip = selected is { } value
            ? string.Join("\n", available.Values
                .OrderBy(entry => entry.Equals(value) ? 0 : 1)
                .ThenByDescending(entry => entry.Percent)
                // Only the first line names the window the icon follows; the tooltip is limited to 127 characters.
                .Select(entry => TooltipLine(entry.Name, entry.Equals(value) ? entry.WindowName : null, entry.Percent, entry.Stale)))
            : _trayProviderId is { } missing
                ? L.T($"{TabDisplayName(missing)}: No data", $"{TabDisplayName(missing)}: Veri yok")
                : L.T("Ration: No data", "Ration: Veri yok");

        _currentGaugePercent = gaugePercent;
        _currentGaugeStale = selected?.Stale ?? false;
        _currentTooltip = tooltip;
        UpdateTrayIcon(gaugePercent, tooltip, _currentGaugeStale);
    }
}
