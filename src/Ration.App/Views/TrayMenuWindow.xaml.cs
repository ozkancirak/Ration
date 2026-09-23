using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Ration.Core.Diagnostics;
using Ration.Platform.Windows.Interop;
using Ration.Platform.Windows.Theme;

namespace Ration.App.Views;

/// <summary>
/// Native tepsi menüsü: WinForms ContextMenuStrip yerine geçen WinUI penceresi.
/// Öğeler: Yenile · Ayarlar · (ayraç) · Çıkış. Klavye: oklar + Enter + Esc.
/// Çalıştırma sırası: ÖNCE Hide(), SONRA eylem — aksi halde Ayarlar penceresi
/// her zaman üstte menünün arkasında açılır.
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private bool _isVisible;

    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public bool IsMenuVisible => _isVisible;
    public IReadOnlyList<Button> ButtonsForSelfTest =>
        new[] { RefreshButton, SettingsButton, ExitButton };

    public TrayMenuWindow()
    {
        InitializeComponent();
        AppTheme.Apply(RootLayout);

        var (appWindow, hwnd) = PopoverHelper.Attach(this);
        _appWindow = appWindow;
        _hwnd = hwnd;

        PopoverHelper.ConfigureChrome(this, _appWindow, _hwnd);
        PopoverHelper.ConfigureDismissal(this, _appWindow, HideMenu, RootLayout);

        RefreshButton.Click += (s, e) =>
        {
            HideMenu();
            Trace.Info("menu", "click action=Yenile");
            RefreshRequested?.Invoke();
        };

        SettingsButton.Click += (s, e) =>
        {
            HideMenu();
            Trace.Info("menu", "click action=Ayarlar");
            SettingsRequested?.Invoke();
        };

        ExitButton.Click += (s, e) =>
        {
            HideMenu();
            Trace.Info("menu", "click action=exit");
            ExitRequested?.Invoke();
        };

        // Tema canlı değişimi: akrilik kendiliğinden uyar; ikon/ayraç
        // fırçaları ThemeResource'tan yeniden alınır.
        WindowsThemeListener.ThemeChanged += OnSystemThemeChanged;
        WindowsThemeListener.AccentChanged += () => this.DispatcherQueue.TryEnqueue(RefreshChrome);
        AppThemePreference.Changed += OnAppThemeChanged;

        _appWindow.Resize(new SizeInt32(160, 120));
        _isVisible = false;
    }

    private void OnSystemThemeChanged(bool isLightTheme)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            AppTheme.Apply(RootLayout);
            RefreshChrome();
        });
    }

    private void OnAppThemeChanged(AppThemeMode mode)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            AppTheme.Apply(RootLayout);
            RefreshChrome();
        });
    }

    private void RefreshChrome()
    {
        foreach (var button in ButtonsForSelfTest)
        {
            if (button.Content is StackPanel row
                && row.Children.FirstOrDefault() is Viewbox box
                && box.Child is FontIcon icon)
            {
                icon.Foreground = QuotaVisuals.Fill("TextFillColorTertiaryBrush");
            }
        }

        MenuSeparator.Background = QuotaVisuals.Fill("CardStrokeColorDefaultBrush");
    }

    public void ShowAtCursor()
    {
        AppTheme.Apply(RootLayout);
        var (physW, physH) = MeasureMenu(out int dipW, out int dipH);

        var (x, y) = FlyoutPositioner.CalculatePosition(
            Guid.Empty,
            _hwnd,
            0,
            physW,
            physH,
            out var edge);

        _appWindow.MoveAndResize(new RectInt32(x, y, physW, physH));
        PopoverHelper.ShowPopover(_appWindow, this, _hwnd, RootLayout, edge);
        _isVisible = true;
        RefreshButton.Focus(FocusState.Programmatic);
        Trace.Info("menu", "show");

        // İlk karede öğeler henüz gerçekleşmemiştir; yerleşim bitince
        // yeniden ölç ve boyu düzelt (konum sabit kalır).
        this.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (!_isVisible) return;
                var (w2, h2) = MeasureMenu(out _, out _);
                _appWindow.ResizeClient(new SizeInt32(w2, h2));
            });
    }

    private (int PhysW, int PhysH) MeasureMenu(out int dipW, out int dipH)
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        double scale = dpi / 96.0;

        RootLayout.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        dipW = Math.Max(160, (int)Math.Round(RootLayout.DesiredSize.Width));
        dipH = Math.Min(
            Math.Max(1, (int)Math.Round(RootLayout.DesiredSize.Height)),
            PopoverHelper.WorkAreaMaxHeight());
        return (Math.Max(1, (int)Math.Round(dipW * scale)), Math.Max(1, (int)Math.Round(dipH * scale)));
    }

    public void HideMenu()
    {
        if (!_isVisible) return;
        _isVisible = false;
        PopoverHelper.HidePopover(_appWindow, RootLayout);
        Trace.Info("menu", "hide");
    }

    /// <summary>
    /// UIA kullanmadan self-test'in gerçek fare tıklaması için bir düğmenin
    /// ekran dikdörtgenini hesaplar. XAML ölçüleri DIP, AppWindow konumu ise
    /// fiziksel pikseldir; DPI dönüşümü bu sınırda yapılır.
    /// </summary>
    public NativeMethods.RECT GetButtonScreenRect(Button button)
    {
        var transform = button.TransformToVisual(null);
        var dipBounds = transform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;

        var position = _appWindow.Position;
        NativeMethods.GetWindowRect(_hwnd, out var windowRect);
        var rect = new NativeMethods.RECT
        {
            Left = position.X + (int)Math.Round(dipBounds.X * scale),
            Top = position.Y + (int)Math.Round(dipBounds.Y * scale),
            Right = position.X + (int)Math.Round((dipBounds.X + dipBounds.Width) * scale),
            Bottom = position.Y + (int)Math.Round((dipBounds.Y + dipBounds.Height) * scale),
        };

        string name = AutomationProperties.GetName(button);
        Trace.Info(
            "selftest",
            $"rect window={windowRect.Left},{windowRect.Top},{windowRect.Width}x{windowRect.Height} "
            + $"button={name} x={rect.Left} y={rect.Top} w={rect.Width} h={rect.Height}");
        return rect;
    }
}
