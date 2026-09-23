using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Windows.UI.Notifications;
using Ration.Core.Diagnostics;
using Ration.Core.Model;
using Ration.Core.Usage;

namespace Ration.App.Views;

/// <summary>
/// Kota bildirimlerini Windows bildirimi olarak gösterir. Karar Core'daki
/// <see cref="QuotaAlerts"/>'tadır; burası yalnızca gösterir ve hangi bildirimin zaten
/// gösterildiğini hatırlar (uygulama yeniden başlasa da aynı döngüde tekrarlanmasın).
/// Ayar ve tetiklenen anahtarlar tek dosyada: %LOCALAPPDATA%\Ration\notifications.json.
/// </summary>
internal static class QuotaNotifier
{
    private const int MaxRemembered = 200;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ration", "notifications.json");

    // Paketsiz uygulamanın bildirim kimliği; HKCU\Software\Classes\AppUserModelId altında.
    // Windows App SDK AppNotificationManager self-contained/paketsiz yapıda 0x8007007E
    // (modül bulunamadı) veriyordu; SDK'dan bağımsız Windows toast API'si kullanılır.
    private const string Aumid = "Ration.QuotaTray";

    private static readonly List<string> _fired;
    private static bool _registered;
    private static Action? _onActivated;

    public static bool Enabled { get; private set; }

    static QuotaNotifier()
    {
        (Enabled, _fired) = Load();
    }

    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        Save();
    }

    /// <summary>Bildirime tıklanınca paneli açmak için; kayıt tek sefer yapılır.</summary>
    public static void Register(Action onActivated)
    {
        if (_registered) return;
        try
        {
            // Bildirim merkezinde görünen ad ve ikon; Ration'ın kendi anahtarı.
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
        if (!Enabled || !_registered) return;

        var alerts = QuotaAlerts.Evaluate(snapshot, providerName, _fired.ToHashSet(), DateTimeOffset.UtcNow);
        if (alerts.Count == 0) return;

        foreach (var alert in alerts)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(alert.Title));
                texts[1].AppendChild(xml.CreateTextNode(alert.Body));

                // Tepsi uygulaması sürekli çalıştığı için tıklama süreç içinde yakalanır.
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
        Save();
    }

    private static (bool Enabled, List<string> Fired) Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return (true, new List<string>());
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            var enabled = root?["enabled"]?.GetValue<bool>() ?? true;
            var fired = root?["fired"] is JsonArray array
                ? array.Select(item => item?.GetValue<string>()).OfType<string>().ToList()
                : new List<string>();
            return (enabled, fired);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Trace.Error("notify", $"settings-read failed type={ex.GetType().Name}");
            return (true, new List<string>());
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var root = new JsonObject
            {
                ["enabled"] = Enabled,
                ["fired"] = new JsonArray(_fired.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
            };
            File.WriteAllText(SettingsPath, root.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.Error("notify", $"settings-write failed type={ex.GetType().Name}");
        }
    }
}
