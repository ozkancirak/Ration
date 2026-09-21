using System.Text.Json;
using Kalan.Core.Providers;

namespace Kalan.Core.Cost;

/// <summary>
/// Sağlayıcı iç model adlarını LiteLLM anahtarlarına bağlayan, kullanıcı tarafından
/// güncellenebilir tablo. Varsayılan dosya uygulamanın Assets klasöründen ilk okumada
/// Kalan önbelleğine kopyalanır.
/// </summary>
public sealed class ModelAliasTable
{
    private readonly IReadOnlyDictionary<string, string> _aliases;

    private ModelAliasTable(IReadOnlyDictionary<string, string> aliases) => _aliases = aliases;

    public static ModelAliasTable LoadOrEmpty(string? path = null)
    {
        path ??= KnownPaths.ModelAliasesFile;
        EnsureDefaultFile(path);
        if (!File.Exists(path)) return new ModelAliasTable(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ModelAliasTable(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                var target = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(target))
                {
                    aliases[property.Name] = target.Trim();
                }
            }

            return new ModelAliasTable(aliases);
        }
        catch (JsonException) { return Empty; }
        catch (IOException) { return Empty; }
        catch (UnauthorizedAccessException) { return Empty; }
    }

    public string Resolve(string model) =>
        _aliases.TryGetValue(model, out var alias) ? alias : model;

    private static ModelAliasTable Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static void EnsureDefaultFile(string destination)
    {
        if (File.Exists(destination)) return;

        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "model-aliases.json");
        if (!File.Exists(bundled)) return;

        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.Copy(bundled, destination, overwrite: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
