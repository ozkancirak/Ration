using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Ration.Core.Diagnostics;
using Ration.Core.Layout;
using Ration.Platform.Windows.Interop;
using Ration.Platform.Windows.Theme;

namespace Ration.App.Views;

/// <summary>
/// Native tray menu: a WinUI window that replaces the WinForms ContextMenuStrip.
/// Items: Refresh · Settings · (separator) · Exit. Keyboard: arrows + Enter + Esc.
/// Execution order: Hide() FIRST, THEN the action — otherwise the Settings window opens
/// behind the always-on-top menu.
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

        // Live theme change: acrylic adapts by itself; the icon/separator
        // brushes are re-fetched from the ThemeResource.
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

    /// <summary>The small dot on the Settings row: a new version is available.</summary>
    public void SetUpdateAvailable(bool available)
    {
        SettingsUpdateDot.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(
            SettingsButton,
            available
                ? L.T("Settings, an update is available", "Ayarlar, güncelleme var")
                : L.T("Settings", "Ayarlar"));
    }

    public void ShowAtCursor()
    {
        AppTheme.Apply(RootLayout);
        // The DPI is taken from the target screen: the window may currently be on a screen with a different scale.
        var anchor = FlyoutPositioner.ResolveAnchor(Guid.Empty, _hwnd, 0);
        var (physW, physH) = MeasureMenu(anchor?.Dpi ?? WindowDpi(), out _, out _);

        var placement = anchor?.Place(physW, physH) ?? new FlyoutPlacement(100, 100, FlyoutEdge.Bottom);
        var bounds = new RectInt32(placement.X, placement.Y, physW, physH);

        _appWindow.MoveAndResize(bounds);
        if (anchor is { } known && NativeMethods.GetDpiForWindow(_hwnd) != known.Dpi) _appWindow.MoveAndResize(bounds);
        PopoverHelper.ShowPopover(_appWindow, this, _hwnd, RootLayout, placement.Edge);
        _isVisible = true;
        RefreshButton.Focus(FocusState.Programmatic);
        Trace.Info("menu", "show");

        // In the first frame the items are not realized yet; once layout is done,
        // re-measure and fix the height (the position stays fixed).
        this.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (!_isVisible) return;
                var (w2, h2) = MeasureMenu(WindowDpi(), out _, out _);
                _appWindow.ResizeClient(new SizeInt32(w2, h2));
            });
    }

    private uint WindowDpi()
    {
        var dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        return dpi == 0 ? 96 : dpi;
    }

    private (int PhysW, int PhysH) MeasureMenu(uint dpi, out int dipW, out int dipH)
    {
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
    /// Computes the screen rectangle of a button for the self-test's real mouse click without using UIA.
    /// XAML measurements are DIP while the AppWindow position is in physical pixels;
    /// the DPI conversion is done at this boundary.
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
