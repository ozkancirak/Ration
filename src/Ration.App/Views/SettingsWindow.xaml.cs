using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using CommunityToolkit.WinUI.Controls;
using Ration.Core.Cost;
using Ration.Core.Diagnostics;
using Ration.Core.Providers;
using Ration.Core.Providers.Claude;
using Ration.Core.Providers.Codex;
using Ration.Platform.Windows.Interop;
using Ration.Platform.Windows.App;
using Ration.Platform.Windows.Theme;

namespace Ration.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    public event Action<string?>? TrayProviderChanged;

    public SettingsWindow()
    {
        InitializeComponent();
        AppTheme.Apply(SettingsScrollViewer);
        RefreshProviderIcons();

        var savedTrayProvider = TrayProviderPreference.Current ?? "auto";
        TrayProviderSelection.SelectedItem = TrayProviderSelection.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, savedTrayProvider, StringComparison.OrdinalIgnoreCase))
            ?? TrayProviderSelection.Items[0];
        TrayProviderSelection.SelectionChanged += (s, e) =>
        {
            if (TrayProviderSelection.SelectedItem is ComboBoxItem { Tag: string providerId })
            {
                var selected = providerId.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : providerId;
                TrayProviderPreference.Set(selected);
                TrayProviderChanged?.Invoke(selected);
            }
        };

        RefreshIntervalSelection.SelectedItem = RefreshIntervalSelection.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                int.TryParse(item.Tag as string, out var minutes) &&
                minutes == RefreshIntervalPreference.ToMinutes(RefreshIntervalPreference.Current));
        RefreshIntervalSelection.SelectionChanged += (_, _) =>
        {
            if (RefreshIntervalSelection.SelectedItem is ComboBoxItem { Tag: string value } &&
                int.TryParse(value, out var minutes))
            {
                var interval = RefreshIntervalPreference.FromMinutes(minutes);
                RefreshIntervalPreference.Set(interval);
            }
        };

        ThemeSelection.SelectedItem = ThemeSelection.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(
                    item.Tag as string,
                    AppThemePreference.ToTag(AppThemePreference.Current),
                    StringComparison.OrdinalIgnoreCase));
        ThemeSelection.SelectionChanged += (_, _) =>
        {
            if (ThemeSelection.SelectedItem is ComboBoxItem { Tag: string tag })
            {
                AppThemePreference.Set(AppThemePreference.FromTag(tag));
            }
        };
        AppThemePreference.Changed += OnAppThemeChanged;

        StartupToggle.IsOn = StartupRegistration.IsEnabled();
        StartupToggle.Toggled += (_, _) =>
        {
            bool requested = StartupToggle.IsOn;
            if (!StartupRegistration.SetEnabled(requested))
            {
                StartupToggle.IsOn = !requested;
            }
        };

        CheckUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync();
        OpenLogButton.Click += (_, _) => OpenLog();
        CopyStatusLineButton.Click += (_, _) => CopyStatusLineSnippet();
        VersionText.Text = $"Sürüm {UpdateService.CurrentVersion}";
        UpdateStatusText.Text = UpdateStatusTextFor(UpdateService.LastResult);

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

        // Tur 5: Başlangıç 520×640; içerik ScrollViewer ile dar yükseklikte kayar.
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        double scale = dpi / 96.0;

        int width = (int)Math.Round(520 * scale);
        int height = (int)Math.Round(640 * scale);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(440 * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(420 * scale);
        }

        _appWindow.Resize(new SizeInt32(width, height));

        // Pencere çerçevesi için Immersive Dark Mode
        UpdateWindowFrameTheme();
        WindowsThemeListener.ThemeChanged += OnThemeChanged;

        RefreshCostStatus();
        RefreshProviderStatus();

        _appWindow.Closing += (s, e) =>
        {
            // Tamamen kapatmak yerine gizle; tray'den tıklandığında anında açılsın
            e.Cancel = true;
            _appWindow.Hide();
            Trace.Info("window", "settings.hide");
        };
    }

    private void OnThemeChanged(bool isLightTheme)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            AppTheme.Apply(SettingsScrollViewer);
            UpdateWindowFrameTheme();
            RefreshProviderIcons();
        });
    }

    private void OnAppThemeChanged(AppThemeMode mode)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            AppTheme.Apply(SettingsScrollViewer);
            ThemeSelection.SelectedItem = ThemeSelection.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item =>
                    string.Equals(item.Tag as string, AppThemePreference.ToTag(mode), StringComparison.OrdinalIgnoreCase));
            UpdateWindowFrameTheme();
            RefreshProviderIcons();
        });
    }

    private void RefreshProviderIcons()
    {
        SetProviderHeader(ClaudeProviderExpander, "claude", "Claude Code");
        SetProviderHeader(CodexProviderExpander, "codex", "Codex");
        SetProviderHeader(AntigravityProviderExpander, "antigravity", "Antigravity");
        SetProviderHeader(OpenCodeProviderExpander, "opencode", "OpenCode");
    }

    /// <summary>
    /// Açıklama expander'ın kendi alanında dururken ikonun altına düşüyordu; başlığın
    /// içine, adın altına taşınır. XAML'daki metin ilk seferde Tag'e saklanır.
    /// </summary>
    private static void SetProviderHeader(SettingsExpander expander, string providerId, string name)
    {
        var description = expander.Tag as string ?? expander.Description as string;
        expander.Tag = description;
        expander.ClearValue(SettingsExpander.DescriptionProperty);
        expander.Header = ProviderIcons.CreateHeader(providerId, name, description);
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

        // İçerik başlık çubuğuna uzatıldığı için kapat/büyüt düğmeleri sistem temasını
        // izliyordu: koyu tema zorlanınca koyu zeminde siyah kalıyorlardı.
        _appWindow.TitleBar.PreferredTheme = isDark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    public bool IsShown => _appWindow.IsVisible;

    public void ShowAndFocus()
    {
        AppTheme.Apply(SettingsScrollViewer);
        RefreshCostStatus();
        RefreshProviderStatus();
        _appWindow.Show();
        this.Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
        Trace.Info("window", "settings.show");
    }

    public void HideForSelfTest()
    {
        _appWindow.Hide();
        Trace.Info("window", "settings.hide selftest");
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

    /// <summary>
    /// ~/.claude/settings.json'a eklenecek statusLine satırını panoya koyar. Ration o dosyaya
    /// kendisi yazmaz (AGENTS.md §2.1); kullanıcı yapıştırır.
    /// </summary>
    private async void CopyStatusLineSnippet()
    {
        var exe = Environment.ExpandEnvironmentVariables(StartupRegistration.TargetValue.Trim('"'));
        var options = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var snippet = "\"statusLine\": " + new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "command",
            ["command"] = $"\"{exe}\" --statusline",
        }.ToJsonString(options);

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(snippet);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Trace.Info("settings", "statusline snippet copied");

        CopyStatusLineButton.Content = "Kopyalandı";
        await Task.Delay(TimeSpan.FromSeconds(2));
        CopyStatusLineButton.Content = "Satırı kopyala";
    }

    private void RefreshProviderStatus()
    {
        var statusLine = ClaudeStatusLine.Read();
        SetProviderStatus(
            ClaudeStatusLineBadge,
            ClaudeStatusLineStatus,
            statusLine is not null,
            statusLine is { } record ? $"Bağlı · {QuotaVisuals.FormatUpdated(record.CapturedAt).Replace(" güncellendi", string.Empty)}" : string.Empty,
            "Bağlı değil");

        var claude = ClaudeCredentialStore.TryRead();
        SetProviderStatus(
            ClaudeProviderBadge,
            ClaudeProviderStatus,
            claude is { IsExpired: false },
            "Kimlik bulundu",
            // Süresi geçmiş oturum da bir sorundur: uyarı rozetiyle gösterilir (yeşil yanıltıyordu).
            claude is null ? "Kimlik bulunamadı" : "Oturum süresi geçmiş — Claude Code'u çalıştırın");

        var codex = CodexCredentialStore.TryRead();
        SetProviderStatus(
            CodexProviderBadge,
            CodexProviderStatus,
            codex is not null,
            "Kimlik bulundu",
            "Kimlik bulunamadı");

        // Antigravity verisi yerel dil sunucusundan gelir; uygulama kapalıyken güncellenmez.
        var antigravityRunning = System.Diagnostics.Process.GetProcessesByName("Antigravity").Length > 0 ||
            System.Diagnostics.Process.GetProcessesByName("language_server").Length > 0;
        SetProviderStatus(
            AntigravityProviderBadge,
            AntigravityProviderStatus,
            antigravityRunning,
            "Açık",
            "Kapalı — açınca veri gelir");

        SetProviderStatus(
            OpenCodeProviderBadge,
            OpenCodeProviderStatus,
            File.Exists(KnownPaths.OpenCodeDatabaseFile),
            "Veri bulundu",
            "Veritabanı bulunamadı");
    }

    private async Task CheckForUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Denetleniyor…";
        try
        {
            var result = await UpdateService.CheckAsync();
            UpdateStatusText.Text = UpdateStatusTextFor(result);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private static string UpdateStatusTextFor(UpdateCheckResult? result)
    {
        if (result is null) return "Henüz denetlenmedi";
        if (result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.AvailableVersion))
        {
            return $"Sürüm {result.AvailableVersion} hazır";
        }

        var checkedAt = result.CheckedAt.ToLocalTime();
        return result.Succeeded
            ? $"Güncel · {checkedAt:HH:mm}'de denetlendi"
            : $"Denetlenemedi · {checkedAt:HH:mm}'de denetlendi";
    }

    private static void OpenLog()
    {
        try
        {
            Trace.Info("diagnostics", "log-open");
            Process.Start(new ProcessStartInfo
            {
                FileName = Trace.LogPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Trace.Error("diagnostics", $"log-open failed type={ex.GetType().Name}");
        }
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

    private static void SetProviderStatus(
        InfoBadge badge,
        TextBlock status,
        bool available,
        string availableText,
        string unavailableText)
    {
        status.Text = available ? availableText : unavailableText;
        var styleKey = available ? "SuccessDotInfoBadgeStyle" : "AttentionDotInfoBadgeStyle";
        if (Application.Current.Resources.TryGetValue(styleKey, out var style) &&
            style is Style providerStyle)
        {
            badge.Style = providerStyle;
        }
    }
}
