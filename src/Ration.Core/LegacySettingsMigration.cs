namespace Ration.Core;

/// <summary>Moves only user preferences from the previous product directory.</summary>
public static class LegacySettingsMigration
{
    // Compatibility identifier: required to locate the previous installation.
    public const string LegacyProductName = "Kalan";

    public static void Run(string? localAppData = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var source = Path.Combine(localAppData, LegacyProductName);
        var destination = Path.Combine(localAppData, "Ration");
        if (!Directory.Exists(source) || Directory.Exists(destination)) return;

        try
        {
            Directory.CreateDirectory(destination);
            // Explicit allowlist: never migrate snapshots, prices or credentials.
            foreach (var name in new[] { "theme.json", "refresh.json", "settings.json" })
            {
                var path = Path.Combine(source, name);
                if (File.Exists(path)) File.Move(path, Path.Combine(destination, name), overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Trace.Error("migration", $"preferences type={ex.GetType().Name}");
        }
    }
}
