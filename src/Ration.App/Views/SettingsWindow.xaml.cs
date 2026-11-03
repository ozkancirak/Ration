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
using Ration.Core.Providers.Cursor;
using Ration.Core.Settings;
using Ration.Core.Updates;
using Ration.App.Updates;
using Ration.Platform.Windows.Interop;
using Ration.Platform.Windows.App;
using Ration.Platform.Windows.Providers;
using Ration.Platform.Windows.Theme;

namespace Ration.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    public event Action<string?>? TrayProviderChanged;

    /// <summary>The effective language changed; the app must restart (FlyoutWindow does it).</summary>
    public event Action? LanguageChanged;

    /// <summary>The update was scheduled to be applied on exit; the app must close (FlyoutWindow does it).</summary>
    public event Action? UpdateRestartRequested;

    /// <summary>The settings were deleted; since theme and language are applied at startup the app must restart.</summary>
    public event Action? SettingsReset;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = L.T("Settings — Ration", "Ayarlar — Ration");
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

        TrayWindowSelection.SelectedItem = TrayWindowSelection.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == TrayWindowPreference.ToTag(TrayWindowPreference.Current));
        TrayWindowSelection.SelectionChanged += (_, _) =>
        {
            if (TrayWindowSelection.SelectedItem is ComboBoxItem { Tag: string tag })
            {
                TrayWindowPreference.Set(TrayWindowPreference.FromTag(tag));
            }
        };

        UsageDisplaySelection.SelectedIndex = UsageDisplayPreference.ShowUsed ? 1 : 0;
        UsageDisplaySelection.SelectionChanged += (_, _) =>
            UsageDisplayPreference.Set(UsageDisplaySelection.SelectedItem is ComboBoxItem { Tag: "used" });

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

        var language = L.Preference;
        LanguageSelection.SelectedItem = LanguageSelection.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, L.ToTag(language), StringComparison.OrdinalIgnoreCase));
        LanguageSelection.SelectionChanged += (_, _) =>
        {
            if (LanguageSelection.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            var selected = L.FromTag(tag);
            L.Preference = selected;
            // If the "System" choice does not change the effective language, no restart is needed.
            if (L.Resolve(selected) != L.Turkish) LanguageChanged?.Invoke();
        };

        StartupToggle.IsOn = StartupRegistration.IsEnabled();
        StartupToggle.Toggled += (_, _) =>
        {
            bool requested = StartupToggle.IsOn;
            if (!StartupRegistration.SetEnabled(requested))
            {
                StartupToggle.IsOn = !requested;
            }
        };

        ShowHotkey();
        HotkeyToggle.IsOn = HotkeyPreference.Enabled;
        HotkeyToggle.Toggled += (_, _) => HotkeyPreference.SetEnabled(HotkeyToggle.IsOn);
        HotkeyButton.Click += (_, _) => BeginHotkeyCapture();
        HotkeyButton.PreviewKeyDown += OnHotkeyKeyDown;
        HotkeyButton.LostFocus += (_, _) => EndHotkeyCapture();
        HotkeyPreference.RegistrationChanged += () => DispatcherQueue.TryEnqueue(ShowHotkey);

        NotificationsToggle.IsOn = QuotaNotifier.Enabled;
        NotificationsToggle.Toggled += (_, _) =>
        {
            QuotaNotifier.SetEnabled(NotificationsToggle.IsOn);
            SetNotificationOptionsEnabled(NotificationsToggle.IsOn);
        };

        foreach (var (toggle, providerId) in NotificationProviderToggles())
        {
            toggle.IsOn = QuotaNotifier.IsProviderEnabled(providerId);
            toggle.Toggled += (_, _) => QuotaNotifier.SetProviderEnabled(providerId, toggle.IsOn);
        }

        ResetNotifyToggle.IsOn = QuotaNotifier.NotifyOnReset;
        ResetNotifyToggle.Toggled += (_, _) => QuotaNotifier.SetNotifyOnReset(ResetNotifyToggle.IsOn);

        SetNotificationOptionsEnabled(QuotaNotifier.Enabled);
        LowThresholdSelection.SelectedItem = LowThresholdSelection.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == QuotaNotifier.LowThreshold.ToString());
        LowThresholdSelection.SelectionChanged += (_, _) =>
        {
            if (LowThresholdSelection.SelectedItem is ComboBoxItem { Tag: string value } &&
                int.TryParse(value, out var percent))
            {
                QuotaNotifier.SetLowThreshold(percent);
            }
        };

        PrereleaseToggle.IsOn = AppUpdates.IncludePrerelease;
        PrereleaseToggle.Toggled += async (_, _) =>
        {
            AppUpdates.IncludePrerelease = PrereleaseToggle.IsOn;
            // When the channel changes the previous result (e.g. a beta) may no longer be valid.
            await CheckForUpdatesAsync();
        };
        CheckUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync();
        InstallUpdateButton.Click += async (_, _) => await DownloadUpdateAsync();
        OpenLogButton.Click += (_, _) => OpenLog();
        OpenDataFolderButton.Click += (_, _) => OpenDataFolder();
        ResetSettingsButton.Click += async (_, _) => await ResetSettingsAsync();
        CopyDiagnosticsButton.Click += async (_, _) => await CopyDiagnosticsAsync();
        CopyStatusLineButton.Click += (_, _) => CopyStatusLineSnippet();
        VersionText.Text = L.T($"Version {AppUpdates.CurrentVersion}", $"Sürüm {AppUpdates.CurrentVersion}");
        ShowUpdateState(AppUpdates.Service.LastResult);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

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

        // ExtendsContentIntoTitleBar = true, custom title bar with SetTitleBar
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);

        // Round 5: Initial 520×640; the content scrolls with a ScrollViewer at narrow heights.
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        double scale = dpi / 96.0;

        int width = (int)Math.Round(680 * scale);
        int height = (int)Math.Round(640 * scale);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(440 * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(420 * scale);
        }

        _appWindow.Resize(new SizeInt32(width, height));

        // Immersive Dark Mode for the window frame
        UpdateWindowFrameTheme();
        WindowsThemeListener.ThemeChanged += OnThemeChanged;

        RefreshCostStatus();
        RefreshProviderStatus();

        _appWindow.Closing += (s, e) =>
        {
            // Hide instead of closing completely; so it opens instantly when clicked from the tray
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
        SetProviderHeader(CursorProviderExpander, "cursor", "Cursor");
    }

    /// <summary>
    /// The description stayed in the expander's own area and dropped under the icon; it is moved into the
    /// header, under the name. The text in the XAML is saved to Tag the first time.
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

        // Since the content is extended into the title bar, the close/maximize buttons followed the system
        // theme: when the dark theme was forced they stayed black on a dark background.
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
    /// The real state of the cost scan: the scan always runs regardless of settings (there is no off
    /// switch, so a disabled toggle would lie); the money amount, however, needs pricing.json.
    /// A status label + a single-line reason are written.
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
    /// Puts the statusLine line to be added to ~/.claude/settings.json on the clipboard. Ration does not write
    /// to that file itself; the user pastes it.
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

        var copied = ClipboardHelper.TryCopy(snippet);
        if (copied) Trace.Info("settings", "statusline snippet copied");

        await ShowCopyResultAsync(CopyStatusLineButton, copied, L.T("Copy line", "Satırı kopyala"));
    }

    private void RefreshProviderStatus()
    {
        var statusLine = ClaudeStatusLine.Read();
        SetProviderStatus(
            ClaudeStatusLineBadge,
            ClaudeStatusLineStatus,
            statusLine is not null,
            statusLine is { } record ? L.T("Connected", "Bağlı") + $" · {QuotaVisuals.FormatAge(record.CapturedAt)}" : string.Empty,
            L.T("Not connected", "Bağlı değil"),
            unavailableIsProblem: false);

        var claude = ClaudeCredentialStore.TryRead();
        SetProviderStatus(
            ClaudeProviderBadge,
            ClaudeProviderStatus,
            claude is { IsExpired: false },
            L.T("Credentials found", "Kimlik bulundu"),
            // An expired session is also a problem: shown with a warning badge (green was misleading).
            claude is null ? L.T("No credentials found", "Kimlik bulunamadı") : L.T("Session expired — run Claude Code", "Oturum süresi geçmiş — Claude Code'u çalıştırın"));

        var codex = CodexCredentialStore.TryRead();
        SetProviderStatus(
            CodexProviderBadge,
            CodexProviderStatus,
            codex is not null,
            L.T("Credentials found", "Kimlik bulundu"),
            L.T("No credentials found", "Kimlik bulunamadı"));

        // Antigravity data comes from the local language server; it is not updated while the app is closed.
        var antigravityRunning = IsProcessRunning("Antigravity") || IsProcessRunning("language_server");
        SetProviderStatus(
            AntigravityProviderBadge,
            AntigravityProviderStatus,
            antigravityRunning,
            L.T("Running", "Açık"),
            L.T("Not running — data arrives when opened", "Kapalı — açınca veri gelir"),
            unavailableIsProblem: false);

        SetProviderStatus(
            OpenCodeProviderBadge,
            OpenCodeProviderStatus,
            File.Exists(KnownPaths.OpenCodeDatabaseFile),
            L.T("Data found", "Veri bulundu"),
            L.T("Database not found", "Veritabanı bulunamadı"));

        var cursor = CursorSessionStore.TryRead(decrypt: CursorTokenProtector.TryDecrypt);
        SetProviderStatus(
            CursorProviderBadge,
            CursorProviderStatus,
            cursor?.IsUsable(DateTimeOffset.UtcNow) == true,
            L.T("Session found", "Oturum bulundu"),
            cursor is not null
                ? L.T("Session expired — open Cursor", "Oturum süresi geçmiş — Cursor'ı açın")
                : CursorSessionStore.HasEncryptedToken()
                    ? L.T("Sign-in is encrypted — not readable yet", "Giriş şifreli — henüz okunamıyor")
                    : File.Exists(KnownPaths.CursorStateDatabaseFile)
                        ? L.T("Not signed in", "Giriş yapılmamış")
                        : L.T("Cursor not found", "Cursor bulunamadı"));
    }

    private IEnumerable<(ToggleSwitch Toggle, string ProviderId)> NotificationProviderToggles()
    {
        yield return (ClaudeNotifyToggle, "claude");
        yield return (CodexNotifyToggle, "codex");
        yield return (AntigravityNotifyToggle, "antigravity");
    }

    // Hotkey capture: clicking the button saves the next valid combination. While capturing, the registered
    // hotkey is released (HotkeyPreference.Capturing); otherwise pressing the same combination
    // would never reach the window.
    private bool _capturingHotkey;

    private void ShowHotkey()
    {
        var text = HotkeyPreference.Current.ToString();
        if (!_capturingHotkey) HotkeyButton.Content = text;

        if (HotkeyPreference.RegistrationFailed)
        {
            // Expanded so that it is visible even when the section is collapsed.
            HotkeyExpander.Description = L.T(
                $"{text} is already used by another app. Choose another combination.",
                $"{text} başka bir uygulama tarafından kullanılıyor. Başka bir birleşim seç.");
            HotkeyExpander.IsExpanded = true;
        }
        else
        {
            HotkeyExpander.Description = L.T($"Open Ration from anywhere with {text}", $"Ration’ı her yerden {text} ile aç");
        }
    }

    private void BeginHotkeyCapture()
    {
        _capturingHotkey = true;
        HotkeyPreference.SetCapturing(true);
        HotkeyButton.Content = L.T("Press keys…", "Tuşlara bas…");
    }

    private void EndHotkeyCapture()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        HotkeyPreference.SetCapturing(false);
        ShowHotkey();
    }

    private void OnHotkeyKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            EndHotkeyCapture();
            return;
        }

        int modifiers = 0;
        if (IsKeyDown(Windows.System.VirtualKey.Control)) modifiers |= Hotkey.Control;
        if (IsKeyDown(Windows.System.VirtualKey.Menu)) modifiers |= Hotkey.Alt;
        if (IsKeyDown(Windows.System.VirtualKey.Shift)) modifiers |= Hotkey.Shift;
        if (IsKeyDown(Windows.System.VirtualKey.LeftWindows) || IsKeyDown(Windows.System.VirtualKey.RightWindows)) modifiers |= Hotkey.Win;

        // Only a modifier or an unsupported key: keep capturing.
        if (!Hotkey.TryCreate(modifiers, (int)e.Key, out var hotkey)) return;

        HotkeyPreference.Set(hotkey);
        EndHotkeyCapture();
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // While the master switch is off, the threshold and provider options have no effect.
    private void SetNotificationOptionsEnabled(bool enabled)
    {
        LowThresholdSelection.IsEnabled = enabled;
        ResetNotifyToggle.IsEnabled = enabled;
        foreach (var (toggle, _) in NotificationProviderToggles()) toggle.IsEnabled = enabled;
    }

    private async Task CheckForUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = L.T("Checking…", "Denetleniyor…");
        try
        {
            ShowUpdateState(await AppUpdates.Service.CheckAsync());
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private async Task DownloadUpdateAsync()
    {
        var result = AppUpdates.Service.LastResult;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = L.T("Downloading…", "İndiriliyor…");
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        // Progress<T> captures the UI thread's context; Velopack reports from another thread.
        var progress = new Progress<int>(percent =>
        {
            UpdateProgress.Value = percent;
            UpdateStatusText.Text = L.T($"Downloading… {percent}%", $"İndiriliyor… %{percent}");
        });
        try
        {
            var outcome = await AppUpdates.Service.DownloadAsync(progress);
            if (outcome == UpdateDownloadResult.Downloaded && AppUpdates.Service.ApplyOnExitAndRestart())
            {
                UpdateStatusText.Text = L.T("Restarting to finish the update…", "Güncellemeyi bitirmek için yeniden başlatılıyor…");
                UpdateRestartRequested?.Invoke();
                return;
            }

            // An error does not affect the app: the button stays enabled, the same action can be retried.
            UpdateStatusText.Text = outcome switch
            {
                UpdateDownloadResult.Downloaded =>
                    L.T($"Version {result?.AvailableVersion} downloaded — restart Ration to finish", $"Sürüm {result?.AvailableVersion} indirildi — bitirmek için Ration'ı yeniden başlatın"),
                UpdateDownloadResult.NothingToDownload =>
                    L.T("No newer version found", "Daha yeni bir sürüm bulunamadı"),
                UpdateDownloadResult.Busy =>
                    L.T("A download is already running", "Bir indirme zaten sürüyor"),
                _ => L.T("Download failed — check your connection and try again", "İndirilemedi — bağlantını denetleyip yeniden dene"),
            };
            if (outcome == UpdateDownloadResult.Failed)
            {
                InstallUpdateButton.Content = L.T("Try again", "Yeniden dene");
            }
        }
        finally
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>Writes the status text; the download button appears only when there really is a new version.</summary>
    private void ShowUpdateState(UpdateCheckResult? result)
    {
        UpdateStatusText.Text = UpdateStatusTextFor(result);
        InstallUpdateButton.Content = L.T("Download and install update", "Güncellemeyi indir ve kur");
        InstallUpdateButton.Visibility = AppUpdates.HasNewerUpdate(result) ? Visibility.Visible : Visibility.Collapsed;

        var notes = AppUpdates.HasNewerUpdate(result) ? UpdateLinks.ReleaseNotes(AppUpdates.RepositoryUrl, result!.AvailableVersion) : null;
        ReleaseNotesLink.NavigateUri = notes;
        ReleaseNotesLink.Visibility = notes is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string UpdateStatusTextFor(UpdateCheckResult? result)
    {
        if (result is null) return L.T("Not checked yet", "Henüz denetlenmedi");
        if (AppUpdates.HasNewerUpdate(result))
        {
            return L.T($"Version {result.AvailableVersion} available", $"Sürüm {result.AvailableVersion} hazır");
        }

        var checkedAt = result.CheckedAt.ToLocalTime();
        return result.Succeeded
            ? L.T($"Up to date · checked at {checkedAt:t}", $"Güncel · {checkedAt:HH:mm}'de denetlendi")
            : L.T($"Check failed · at {checkedAt:t}", $"Denetlenemedi · {checkedAt:HH:mm}'de denetlendi");
    }

    /// <summary>GetProcessesByName opens a handle for every result; if not disposed it waits for the finalizer.</summary>
    private static bool IsProcessRunning(string name)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static async Task ShowCopyResultAsync(Button button, bool copied, string idleText)
    {
        button.Content = copied ? L.T("Copied", "Kopyalandı") : L.T("Could not copy", "Kopyalanamadı");
        await Task.Delay(TimeSpan.FromSeconds(2));
        button.Content = idleText;
    }

    private async Task CopyDiagnosticsAsync()
    {
        var report = DiagnosticsReport.Build(
            AppUpdates.CurrentVersion,
            $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
            L.Turkish ? "tr" : "en",
            Trace.ReadLastLines(100),
            Ration.Core.Providers.KnownPaths.Home);

        var copied = ClipboardHelper.TryCopy(report);
        if (copied) Trace.Info("diagnostics", "report copied");

        await ShowCopyResultAsync(CopyDiagnosticsButton, copied, L.T("Copy diagnostics", "Tanılamayı kopyala"));
    }

    /// <summary>
    /// Only settings.json is deleted: language, theme, refresh interval, tray provider, notifications
    /// and update state. Start with Windows, the price table and the log stay in place.
    /// </summary>
    private async Task ResetSettingsAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsScrollViewer.XamlRoot,
            Title = L.T("Reset settings?", "Ayarlar sıfırlansın mı?"),
            Content = L.T(
                "Language, theme, refresh interval, tray provider and notification settings go back to their defaults. Ration restarts.",
                "Dil, tema, yenileme aralığı, tepsi sağlayıcısı ve bildirim ayarları varsayılana döner. Ration yeniden başlar."),
            PrimaryButtonText = L.T("Reset", "Sıfırla"),
            CloseButtonText = L.T("Cancel", "Vazgeç"),
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = SettingsScrollViewer.ActualTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SettingsStore.Default.Clear();
        Trace.Info("settings", "reset");
        SettingsReset?.Invoke();
    }

    private static void OpenDataFolder()
    {
        try
        {
            Trace.Info("settings", "data-folder-open");
            Directory.CreateDirectory(KnownPaths.CacheDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = KnownPaths.CacheDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Trace.Error("settings", $"data-folder-open failed type={ex.GetType().Name}");
        }
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
            status.Text = L.T("Off", "Kapalı");
            reason.Text = L.T("Session log directory not found — nothing to scan.", "Oturum log dizini bulunamadı — taranacak veri yok.");
            reason.Visibility = Visibility.Visible;
            // An attention-colored dot; if the key does not exist in this version, the current dot stays
            // (the label is already correct, no crash).
            if (Application.Current.Resources.TryGetValue("AttentionDotInfoBadgeStyle", out var style) && style is Style dot)
            {
                badge.Style = dot;
            }
            return;
        }

        status.Text = L.T("On", "Açık");
        if (priced)
        {
            reason.Visibility = Visibility.Collapsed;
            return;
        }

        reason.Text = L.T("pricing.json not found — showing tokens only instead of amounts.", "pricing.json bulunamadı — para tutarı yerine yalnızca token gösteriliyor.");
        reason.Visibility = Visibility.Visible;
    }

    private static void SetProviderStatus(
        InfoBadge badge,
        TextBlock status,
        bool available,
        string availableText,
        string unavailableText,
        bool unavailableIsProblem = true)
    {
        status.Text = available ? availableText : unavailableText;
        // A problem (expired session, no credentials) gets a yellow warning; a non-problem state (not connected,
        // app closed) is neutral. They were all shown with the same blue badge before.
        var styleKey = available
            ? "SuccessDotInfoBadgeStyle"
            : unavailableIsProblem ? "CautionDotInfoBadgeStyle" : "AttentionDotInfoBadgeStyle";
        if (Application.Current.Resources.TryGetValue(styleKey, out var style) &&
            style is Style providerStyle)
        {
            badge.Style = providerStyle;
        }
    }
}
