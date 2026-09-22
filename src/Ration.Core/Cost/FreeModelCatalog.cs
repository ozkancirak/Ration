using System.Text.Json;
using Ration.Core.Providers;

namespace Ration.Core.Cost;

public sealed class FreeModelCatalog
{
    private readonly HashSet<string> _models;

    private FreeModelCatalog(IEnumerable<string> models) =>
        _models = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);

    public static FreeModelCatalog LoadOrEmpty(string? path = null)
    {
        path ??= KnownPaths.OpenCodeFreeModelsFile;
        EnsureDefaultFile(path);
        if (!File.Exists(path)) return new FreeModelCatalog(Array.Empty<string>());

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return new FreeModelCatalog(Array.Empty<string>());
            }

            return new FreeModelCatalog(models.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));
        }
        catch (JsonException) { return Empty; }
        catch (IOException) { return Empty; }
        catch (UnauthorizedAccessException) { return Empty; }
    }

    public bool IsFree(string model)
    {
        var normalized = model.Trim();
        return normalized.EndsWith("-free", StringComparison.OrdinalIgnoreCase) ||
               _models.Contains(normalized);
    }

    private static FreeModelCatalog Empty { get; } = new(Array.Empty<string>());

    private static void EnsureDefaultFile(string destination)
    {
        if (File.Exists(destination)) return;

        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "free-models.json");
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
