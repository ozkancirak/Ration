using Microsoft.Win32;
using Windows.UI.Notifications;
using Ration.Core.Diagnostics;
using Ration.Core.Model;
using Ration.Core.Settings;
using Ration.Core.Usage;

namespace Ration.App.Views;

/// <summary>
/// Shows quota notifications as Windows notifications. The decision lives in Core's
/// <see cref="QuotaAlerts"/>; this only displays and remembers which notification was already
/// shown (so that it is not repeated in the same cycle even if the app restarts).
/// The setting and the fired keys are in a single file: %LOCALAPPDATA%\Ration\notifications.json.
/// </summary>
internal static class QuotaNotifier
{
    private const int MaxRemembered = 200;

    // The notification identity of an unpackaged app; under HKCU\Software\Classes\AppUserModelId.
    // Windows App SDK AppNotificationManager gave 0x8007007E (module not found) in a
    // self-contained/unpackaged build; the Windows toast API, which is independent of the SDK, is used.
    private const string Aumid = "Ration.QuotaTray";

    private static readonly List<string> _fired;
    private static bool _registered;
    private static Action? _onActivated;

    public static bool Enabled { get; private set; }

    private const string EnabledKey = "notificationsEnabled";
    private const string FiredKey = "firedAlerts";
    private const string ThresholdKey = "lowQuotaThreshold";
    private const string DisabledProvidersKey = "notificationsDisabledProviders";

    public static readonly int[] Thresholds = [10, 20, 30, 50];

    /// <summary>Remaining-quota threshold (%) for the "running low" notification; an invalid record falls back to the default.</summary>
    public static int LowThreshold { get; private set; }

    static QuotaNotifier()
    {
        Enabled = SettingsStore.Default.GetBool(EnabledKey, true);
        var stored = SettingsStore.Default.GetInt(ThresholdKey, (int)QuotaAlerts.LowRemaining);
        LowThreshold = Array.IndexOf(Thresholds, stored) >= 0 ? stored : (int)QuotaAlerts.LowRemaining;
        _fired = SettingsStore.Default.GetStrings(FiredKey).ToList();
    }

    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        SettingsStore.Default.Set(EnabledKey, enabled);
    }

    /// <summary>Provider notifications are on by default; the ones turned off are kept in a separate list.</summary>
    public static bool IsProviderEnabled(string providerId) =>
        !SettingsStore.Default.GetStrings(DisabledProvidersKey)
            .Contains(providerId, StringComparer.OrdinalIgnoreCase);

    public static void SetProviderEnabled(string providerId, bool enabled)
    {
        var disabled = SettingsStore.Default.GetStrings(DisabledProvidersKey)
            .Where(id => !id.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!enabled) disabled.Add(providerId);

        SettingsStore.Default.Set(DisabledProvidersKey, disabled);
    }

    public static void SetLowThreshold(int percent)
    {
        LowThreshold = percent;
        SettingsStore.Default.Set(ThresholdKey, percent);
    }

    /// <summary>To open the panel when the notification is clicked; registration is done once.</summary>
    public static void Register(Action onActivated)
    {
        if (_registered) return;
        try
        {
            // The name and icon shown in the notification center; Ration's own key.
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
            key.SetValue("DisplayName", "Ration");
            key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.targetsize-48_altform-lightunplated.png"));
            _onActivated = onActivated;
            _registered = true;
        }
        catch (Exception ex)
        {
            Trace.Error("notify", $"register failed type={ex.GetType().Name} hr=0x{ex.HResult:X8}");
        }
    }

    public static void Evaluate(UsageSnapshot snapshot, string providerName)
    {
        if (!Enabled || !_registered || !IsProviderEnabled(snapshot.ProviderId)) return;

        var alerts = QuotaAlerts.Evaluate(snapshot, providerName, _fired.ToHashSet(), DateTimeOffset.UtcNow, LowThreshold);
        if (alerts.Count == 0) return;

        foreach (var alert in alerts)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(alert.Title));
                texts[1].AppendChild(xml.CreateTextNode(alert.Body));

                // Since the tray app runs continuously, the click is caught in-process.
                var toast = new ToastNotification(xml);
                toast.Activated += (_, _) => _onActivated?.Invoke();
                ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
                Trace.Info("notify", $"shown provider={snapshot.ProviderId}");
            }
            catch (Exception ex)
            {
                Trace.Error("notify", $"show failed type={ex.GetType().Name}");
            }

            _fired.Add(alert.Key);
        }

        if (_fired.Count > MaxRemembered) _fired.RemoveRange(0, _fired.Count - MaxRemembered);
        SettingsStore.Default.Set(FiredKey, _fired);
    }
}
