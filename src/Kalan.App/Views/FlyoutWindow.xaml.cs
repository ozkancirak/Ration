using System.Drawing;
using System.Net.Http;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Windows.Graphics;
using Windows.System;
using Kalan.Core.Abstractions;
using Kalan.Core.Cost;
using Kalan.Core.Model;
using Kalan.Core.Providers.Claude;
using Kalan.Core.Providers.Codex;
using Kalan.Core.Refresh;
using Kalan.Core.Usage;
using Kalan.Platform.Windows.Interop;
using Kalan.Platform.Windows.Power;
using Kalan.Platform.Windows.Theme;
using Kalan.Platform.Windows.Tray;

namespace Kalan.App.Views;

public sealed partial class FlyoutWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly SystemTrayHost _tray;
    private readonly TrayMenuWindow _menu;
    private readonly HttpClient _http;
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

    // Sekme şeridi durumu. Tray'e dokunulmaz; ikon hesabı RecalculateTrayIcon'da aynen durur.
    private sealed record TabDef(string Id, string Name, string Glyph);
    private static readonly TabDef[] KnownTabs =
    [
        new("claude", "Claude", "\uE945"),
        new("codex", "Codex", "\uE943"),
    ];
    private const string FallbackTabGlyph = "\uE710";
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

        // Enable translation for composition entrance animation
        ElementCompositionPreview.SetIsTranslationEnabled(RootLayout, true);

        // Işıkla kapanma (odak kaybı/Esc/kapatma) ortak tabanda; zaman damgası
        // aynı tıklamanın pencereyi kapatıp hemen yeniden açmasını önler.

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
            RefreshCost(force: true);
        };

        SettingsButton.Click += (s, e) => OpenSettingsWindow();
        ExitButton.Click += (s, e) => ExitApplication();

        BuildTabs();

        // Menü native WinUI penceresidir (TrayMenuWindow); WinForms menüsü yok.
        // Önce menü kurulur (tray lambdaları ona kapanır).
        _menu = new TrayMenuWindow();
        _menu.RefreshRequested += async () => await _scheduler.RefreshAllAsync();
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
            _settingsWindow.TrayProviderChanged += OnTrayProviderChanged;
        }
        _settingsWindow.ShowAndFocus();
    }

    private void OnTrayProviderChanged(string? providerId)
    {
        _trayProviderId = providerId;
        RecalculateTrayIcon();
    }

    private void ExitApplication()
    {
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"),
            $"[{DateTime.Now:HH:mm:ss}] ExitApplication cagrildi\n");
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

        foreach (var tab in KnownTabs) EnsureTab(tab.Id, tab.Name, tab.Glyph);
        UpdateTabs();
    }

    private void EnsureTab(string id, string? name = null, string? glyph = null)
    {
        if (_tabs.ContainsKey(id)) return;

        var known = KnownTabs.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new FontIcon
        {
            Glyph = glyph ?? known?.Glyph ?? FallbackTabGlyph,
            FontSize = 16,
            Foreground = QuotaVisuals.Fill("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var nameText = new TextBlock
        {
            Text = name ?? known?.Name ?? id,
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
            Padding = new Thickness(8, 6, 8, 6),
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

    private void UpdateTabs()
    {
        var selectedFill = QuotaVisuals.Fill("SubtleFillColorSecondaryBrush");
        var clear = new SolidColorBrush(Colors.Transparent);

        foreach (var (id, (button, meter)) in _tabs)
        {
            var selected = id.Equals(_selectedId, StringComparison.OrdinalIgnoreCase);
            button.IsChecked = selected;
            button.Background = selected ? selectedFill : clear;

            if (!_scheduler.Current.TryGetValue(id, out var snapshot) || snapshot.Windows.Count == 0)
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
            && current.Status is not (ProviderStatus.AuthRequired or ProviderStatus.Error))
        {
            return;
        }

        var pick = _scheduler.Current.Values
            .FirstOrDefault(s => s.Windows.Count > 0 && s.Status is not (ProviderStatus.AuthRequired or ProviderStatus.Error))
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
            DetailUpdated.Text = "Bekleniyor…";
            DetailError.Visibility = Visibility.Collapsed;
            DetailWindows.Children.Clear();
            CostSection.Visibility = Visibility.Collapsed;
            return;
        }

        DetailName.Text = TabDisplayName(snapshot.ProviderId);
        QuotaVisuals.ApplyPlan(DetailPlanBadge, DetailPlanText, snapshot.PlanName);

        // Bayat veri gösteriliyorsa sebep üstte tek satır yazar
        // (örn. hız sınırı + kaç dk önceki veri); taze veride tazelik saati.
        DetailUpdated.Text = snapshot is { Status: ProviderStatus.Degraded, StaleReason: not null }
            ? snapshot.StaleReason
            : QuotaVisuals.FormatUpdated(snapshot.FetchedAt);

        if (snapshot.Status is ProviderStatus.AuthRequired or ProviderStatus.Error)
        {
            DetailErrorTitle.Text = snapshot.Status == ProviderStatus.AuthRequired ? "Oturum Süresi Doldu" : "Kota Alınamadı";
            DetailErrorDetail.Text = snapshot.Status == ProviderStatus.AuthRequired
                ? $"{TabDisplayName(snapshot.ProviderId)} CLI ile tekrar giriş yapın."
                : snapshot.StaleReason ?? "Sunucudan geçerli veri alınamadı.";
            DetailError.Visibility = Visibility.Visible;
            DetailWindows.Children.Clear();
        }
        else
        {
            DetailError.Visibility = Visibility.Collapsed;
            RenderWindows(snapshot);
        }
    }

    private static string TabDisplayName(string providerId) =>
        KnownTabs.FirstOrDefault(t => t.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.Name ?? providerId;

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
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"),
                    $"[Cost] {id} taramasi basarisiz: {t.Exception?.InnerException?.GetType().Name ?? t.Exception?.GetType().Name}\n");
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
        var (x, y) = FlyoutPositioner.CalculatePosition(Guid.Empty, _hwnd, 0, targetWidth, targetHeight);

        EfficiencyModeManager.SetEfficiencyMode(false);
        _appWindow.MoveAndResize(new RectInt32(x, y, targetWidth, targetHeight));
        PopoverHelper.ShowPopover(_appWindow, this, _hwnd);

        _isVisible = true;

        PlayEntranceAnimation();
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

    private void RecalculateTrayIcon()
    {
        var providerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = "Claude",
            ["codex"] = "Codex",
            ["antigravity"] = "Antigravity",
            ["opencode"] = "OpenCode",
        };
        var available = new Dictionary<string, (string Name, double Percent)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, name) in providerNames)
        {
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
                ? $"{providerNames.GetValueOrDefault(missing, missing)}: Veri yok"
                : "Kalan: Veri yok";

        _currentGaugePercent = gaugePercent;
        _currentTooltip = tooltip;
        UpdateTrayIcon(gaugePercent, tooltip);
    }
}
