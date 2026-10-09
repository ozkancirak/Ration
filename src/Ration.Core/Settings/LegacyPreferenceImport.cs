using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ration.Core.Diagnostics;

namespace Ration.Core.Settings;

/// <summary>Eski sürümlerin HKCU\Software\Ration altına yazdığı değerler.</summary>
public interface ILegacyRegistryValues
{
    object? Read(string name);
    void Delete(string name);
}

/// <summary>
/// v0.3.0 öncesi tercihler ayrı dosyalarda (language.json, theme.json, refresh.json, tray.json,
/// notifications.json) ve kayıt defterindeydi. İlk açılışta bunlar SettingsStore'a alınır, eski
/// kaynaklar silinir. Mevcut bir değerin üzerine yazılmaz; bu yüzden her açılışta çalıştırmak
/// güvenlidir ve geri alınacak bir şey bırakmaz.
/// </summary>
public static class LegacyPreferenceImport
{
    private static readonly int[] RefreshMinutes = [5, 15, 30, 60];
    private static readonly string[] Languages = ["system", "en", "tr"];
    private static readonly string[] Themes = ["system", "light", "dark"];
    private const int MaxFired = 200;

    /// <summary>Alınan değer sayısını döndürür.</summary>
    public static int Run(SettingsStore store, string dataDirectory, ILegacyRegistryValues? registry = null)
    {
        var imported = 0;

        imported += ImportFile(dataDirectory, "language.json", root =>
            ImportString(store, "language", root["language"], Languages));
        imported += ImportFile(dataDirectory, "theme.json", root =>
            ImportString(store, "theme", root["theme"], Themes));
        imported += ImportFile(dataDirectory, "refresh.json", root =>
            ImportInt(store, "refreshMinutes", root["minutes"], RefreshMinutes));
        imported += ImportFile(dataDirectory, "tray.json", root =>
            ImportString(store, "trayProvider", root["provider"], allowed: null));
        imported += ImportFile(dataDirectory, "notifications.json", root =>
            ImportBool(store, "notificationsEnabled", root["enabled"]) + ImportFired(store, root["fired"]));

        if (registry is not null) imported += ImportRegistry(store, registry);

        if (imported > 0) Trace.Info("settings", $"legacy preferences imported count={imported}");
        return imported;
    }

    private static int ImportFile(string directory, string name, Func<JsonObject, int> import)
    {
        var path = Path.Combine(directory, name);
        try
        {
            if (!File.Exists(path)) return 0;

            JsonObject? root = null;
            try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
            catch (JsonException) { /* bozuk dosya: alınacak değer yok, yine de silinir */ }

            var count = root is null ? 0 : import(root);
            File.Delete(path);
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Silinemezse bir sonraki açılışta tekrar denenir; değerler zaten alındığı için zararsız.
            Trace.Error("settings", $"legacy {name} failed type={ex.GetType().Name}");
            return 0;
        }
    }

    private static int ImportRegistry(SettingsStore store, ILegacyRegistryValues registry)
    {
        var count = 0;
        try
        {
            var checkedAt = registry.Read("UpdatesLastCheckedUtc") as string;
            if (DateTimeOffset.TryParse(checkedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                && !store.Has("updateCheckedAt"))
            {
                store.Set("updateCheckedAt", when.ToString("O", CultureInfo.InvariantCulture));
                store.Set("updateCheckSucceeded", registry.Read("UpdatesLastSucceeded") is int ok && ok != 0);
                store.Set("updateAvailable", registry.Read("UpdatesUpdateAvailable") is int av && av != 0);
                store.Set("updateInstalled", registry.Read("UpdatesIsInstalled") is int inst && inst != 0);
                var version = registry.Read("UpdatesAvailableVersion") as string;
                store.Set("updateVersion", string.IsNullOrWhiteSpace(version) ? null : version);
                count = 1;
            }

            foreach (var name in new[]
                     {
                         "UpdatesLastCheckedUtc", "UpdatesLastSucceeded", "UpdatesUpdateAvailable",
                         "UpdatesAvailableVersion", "UpdatesIsInstalled",
                     })
            {
                registry.Delete(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Trace.Error("settings", $"legacy registry failed type={ex.GetType().Name}");
        }

        return count;
    }

    private static int ImportString(SettingsStore store, string key, JsonNode? node, string[]? allowed)
    {
        if (store.Has(key) || node is not JsonValue value || !value.TryGetValue<string>(out var text)) return 0;

        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0 || (allowed is not null && !allowed.Contains(text))) return 0;

        store.Set(key, text);
        return 1;
    }

    private static int ImportInt(SettingsStore store, string key, JsonNode? node, int[] allowed)
    {
        if (store.Has(key) || node is not JsonValue value || !value.TryGetValue<int>(out var number) || !allowed.Contains(number))
        {
            return 0;
        }

        store.Set(key, number);
        return 1;
    }

    private static int ImportBool(SettingsStore store, string key, JsonNode? node)
    {
        if (store.Has(key) || node is not JsonValue value || !value.TryGetValue<bool>(out var flag)) return 0;

        store.Set(key, flag);
        return 1;
    }

    private static int ImportFired(SettingsStore store, JsonNode? node)
    {
        if (store.Has("firedAlerts") || node is not JsonArray array) return 0;

        var keys = array
            .Select(item => item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .OfType<string>()
            .TakeLast(MaxFired)
            .ToList();
        if (keys.Count == 0) return 0;

        store.Set("firedAlerts", keys);
        return 1;
    }
}
