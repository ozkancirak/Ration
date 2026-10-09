using System.Text.Json;
using System.Text.Json.Nodes;
using Ration.Core.Diagnostics;
using Ration.Core.Providers;

namespace Ration.Core.Settings;

/// <summary>
/// Kullanıcı tercihlerinin tek dosyası: <c>%LOCALAPPDATA%\Ration\settings.json</c>. Düz bir JSON
/// nesnesidir, anahtarlar camelCase. Bozuk ya da okunamayan dosya boş sayılır; yazma geçici dosya
/// üzerinden yapılır, yarım yazılmış ayar kalmaz.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private JsonObject _root;

    public SettingsStore(string directory)
    {
        _path = System.IO.Path.Combine(directory, "settings.json");
        _root = Read();
    }

    /// <summary>Uygulamanın tek örneği.</summary>
    public static SettingsStore Default { get; } = new(KnownPaths.CacheDir);

    public string Path => _path;

    public string? GetString(string key, string? fallback = null)
    {
        lock (_gate)
        {
            return _root[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
        }
    }

    public int GetInt(string key, int fallback)
    {
        lock (_gate)
        {
            return _root[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;
        }
    }

    public bool GetBool(string key, bool fallback)
    {
        lock (_gate)
        {
            return _root[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
        }
    }

    public IReadOnlyList<string> GetStrings(string key)
    {
        lock (_gate)
        {
            return _root[key] is JsonArray array
                ? array.Select(item => item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
                : [];
        }
    }

    public bool Has(string key)
    {
        lock (_gate) return _root.ContainsKey(key);
    }

    public void Set(string key, string? value) => Write(key, value is null ? null : JsonValue.Create(value));

    public void Set(string key, int value) => Write(key, JsonValue.Create(value));

    public void Set(string key, bool value) => Write(key, JsonValue.Create(value));

    public void Set(string key, IEnumerable<string> values) =>
        Write(key, new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));

    public void Remove(string key) => Write(key, null);

    /// <summary>Dosyayı siler ve bellekteki değerleri sıfırlar.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _root = new JsonObject();
            try { File.Delete(_path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.Error("settings", $"clear failed type={ex.GetType().Name}");
            }
        }
    }

    private void Write(string key, JsonNode? value)
    {
        lock (_gate)
        {
            var before = _root[key]?.ToJsonString();
            if (value is null) _root.Remove(key); else _root[key] = value;
            if (before == _root[key]?.ToJsonString()) return;
            Save();
        }
    }

    private JsonObject Read()
    {
        try
        {
            if (!File.Exists(_path)) return new JsonObject();
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonNode.Parse(stream) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.Error("settings", $"read failed type={ex.GetType().Name}");
            return new JsonObject();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.Error("settings", $"write failed type={ex.GetType().Name}");
        }
    }
}
