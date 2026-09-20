using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.System;
using Kalan.Platform.Windows.Interop;
using Kalan.Platform.Windows.Theme;

namespace Kalan.App.Views;

/// <summary>
/// Native tepsi menüsü: WinForms ContextMenuStrip yerine geçen WinUI penceresi.
/// Öğeler: Yenile · Ayarlar · (ayraç) · Çıkış. Klavye: oklar + Enter + Esc.
/// Çalıştırma sırası: ÖNCE Hide(), SONRA eylem — aksi halde Ayarlar penceresi
/// her zaman üstte menünün arkasında açılır.
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    private sealed record MenuAction(string Label, string Glyph, Action Execute);

    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private bool _isVisible;

    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public bool IsMenuVisible => _isVisible;

    public TrayMenuWindow()
    {
        InitializeComponent();

        var (appWindow, hwnd) = PopoverHelper.Attach(this);
        _appWindow = appWindow;
        _hwnd = hwnd;

        PopoverHelper.ConfigureChrome(this, _appWindow, _hwnd);
        PopoverHelper.ConfigureDismissal(this, _appWindow, HideMenu, RootLayout);

        AddItem("Yenile", "\uE72C", () => RefreshRequested?.Invoke());
        AddItem("Ayarlar", "\uE713", () => SettingsRequested?.Invoke());
        AddSeparator();
        AddItem("Çıkış", "\uE7E8", () => ExitRequested?.Invoke());

        MenuList.ItemClick += (s, e) =>
        {
            if ((e.ClickedItem as ListViewItem)?.Tag is MenuAction action) Execute(action);
        };

        MenuList.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Enter && MenuList.SelectedItem is ListViewItem { Tag: MenuAction action })
            {
                Execute(action);
                e.Handled = true;
            }
        };

        // Tema canlı değişimi: akrilik kendiliğinden uyar; kodla verilen
        // fırçaları (ikon, ayraç) burada tazele. Metinler stil üzerinden canlıdır.
        WindowsThemeListener.ThemeChanged += _ => this.DispatcherQueue.TryEnqueue(RefreshChrome);
        WindowsThemeListener.AccentChanged += () => this.DispatcherQueue.TryEnqueue(RefreshChrome);

        _appWindow.Resize(new SizeInt32(160, 120));
        _isVisible = false;
    }

    private void AddItem(string label, string glyph, Action execute)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Foreground = QuotaVisuals.Fill("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        QuotaVisuals.SetTextStyle(text, "BodyTextBlockStyle");
        row.Children.Add(text);

        var container = new ListViewItem { Content = row, Tag = new MenuAction(label, glyph, execute) };
        AutomationProperties.SetName(container, label);
        MenuList.Items.Add(container);
    }

    private void AddSeparator()
    {
        var line = new Border
        {
            Height = 1,
            Background = QuotaVisuals.Fill("CardStrokeColorDefaultBrush"),
            Margin = new Thickness(0, 4, 0, 4),
        };
        MenuList.Items.Add(new ListViewItem
        {
            Content = line,
            IsEnabled = false,
            MinHeight = 10,
            Padding = new Thickness(0),
        });
    }

    private void Execute(MenuAction action)
    {
        // Önce kapat, sonra çalıştır.
        HideMenu();
        action.Execute();
    }

    private void RefreshChrome()
    {
        foreach (var container in MenuList.Items.OfType<ListViewItem>())
        {
            if (container.Content is StackPanel row)
            {
                foreach (var icon in row.Children.OfType<FontIcon>())
                {
                    icon.Foreground = QuotaVisuals.Fill("TextFillColorSecondaryBrush");
                }
            }
            else if (container.Content is Border line)
            {
                line.Background = QuotaVisuals.Fill("CardStrokeColorDefaultBrush");
            }
        }
    }

    public void ShowAtCursor()
    {
        var (physW, physH) = MeasureMenu(out int dipW, out int dipH);

        var (x, y) = FlyoutPositioner.CalculatePosition(Guid.Empty, _hwnd, 0, physW, physH);

        _appWindow.MoveAndResize(new RectInt32(x, y, physW, physH));
        PopoverHelper.ShowPopover(_appWindow, this, _hwnd);
        _isVisible = true;

        if (MenuList.SelectedItem is null && MenuList.Items.Count > 0)
        {
            MenuList.SelectedIndex = 0;
        }
        MenuList.Focus(FocusState.Programmatic);

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
        _appWindow.Hide();
    }
}
