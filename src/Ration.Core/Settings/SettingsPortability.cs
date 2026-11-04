using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ration.Core.Settings;

/// <summary>How many settings an import applied and how many entries it skipped (unknown key or wrong type).</summary>
public sealed record ImportResult(int Applied, int Skipped);

/// <summary>
/// Settings export and import: a small JSON file with the user's preferences. Only the known preference keys
/// travel; state that belongs to this installation (which alerts already fired, the last update check) and
/// anything unknown stays out. There are no credentials in the settings, and none are ever added to the file.
/// </summary>
public static class SettingsPortability
{
    /// <summary>The largest file an import reads; a real export is under 2 KB.</summary>
    public const int MaxImportBytes = 64 * 1024;

    private const int FormatVersion = 1;

    private enum Kind { String, Int, Bool, Strings }

    private static readonly Dictionary<string, Kind> Preferences = new(StringComparer.Ordinal)
    {
        ["language"] = Kind.String,
        ["theme"] = Kind.String,
        ["refreshMinutes"] = Kind.Int,
        ["trayProvider"] = Kind.String,
        ["trayWindow"] = Kind.String,
        ["showUsed"] = Kind.Bool,
        ["statusChecks"] = Kind.Bool,
        ["hotkeyEnabled"] = Kind.Bool,
        ["hotkey"] = Kind.String,
        ["notificationsEnabled"] = Kind.Bool,
        ["lowQuotaThreshold"] = Kind.Int,
        ["notificationsDisabledProviders"] = Kind.Strings,
        ["notifyOnReset"] = Kind.Bool,
        ["notifyOnSignIn"] = Kind.Bool,
        ["notifyOnSurge"] = Kind.Bool,
        ["surgePoints"] = Kind.Int,
        ["updatePrerelease"] = Kind.Bool,
    };

    public static string Export(SettingsStore store)
    {
        var settings = new JsonObject();
        foreach (var (key, kind) in Preferences)
        {
            if (!store.Has(key)) continue;

            settings[key] = kind switch
            {
                Kind.String => store.GetString(key) is { } text ? JsonValue.Create(text) : null,
                Kind.Int => JsonValue.Create(store.GetInt(key, 0)),
                Kind.Bool => JsonValue.Create(store.GetBool(key, false)),
                _ => new JsonArray(store.GetStrings(key).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            };
        }

        var root = new JsonObject { ["ration"] = FormatVersion, ["settings"] = settings };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Applies the preferences in the file. Null if the file is not a Ration settings file; nothing is written then.</summary>
    public static ImportResult? Import(SettingsStore store, string json)
    {
        if (json.Length > MaxImportBytes) return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj ||
            obj["ration"] is not JsonValue version || !version.TryGetValue<int>(out var v) || v != FormatVersion ||
            obj["settings"] is not JsonObject settings)
        {
            return null;
        }

        int applied = 0, skipped = 0;
        foreach (var (key, node) in settings)
        {
            if (!Preferences.TryGetValue(key, out var kind) || !Apply(store, key, kind, node))
            {
                skipped++;
                continue;
            }

            applied++;
        }

        return new ImportResult(applied, skipped);
    }

    private static bool Apply(SettingsStore store, string key, Kind kind, JsonNode? node)
    {
        switch (kind)
        {
            case Kind.String when node is JsonValue s && s.TryGetValue<string>(out var text):
                store.Set(key, text);
                return true;
            case Kind.Int when node is JsonValue i && i.TryGetValue<int>(out var number):
                store.Set(key, number);
                return true;
            case Kind.Bool when node is JsonValue b && b.TryGetValue<bool>(out var flag):
                store.Set(key, flag);
                return true;
            case Kind.Strings when node is JsonArray array && array.All(item => item is JsonValue iv && iv.TryGetValue<string>(out _)):
                store.Set(key, array.Select(item => item!.GetValue<string>()).ToList());
                return true;
            default:
                return false;
        }
    }
}
