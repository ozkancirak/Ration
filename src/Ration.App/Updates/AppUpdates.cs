using Ration.Core.Updates;

namespace Ration.App.Updates;

/// <summary>Uygulamanın tek güncelleme servisi ve sürüm bilgisi.</summary>
internal static class AppUpdates
{
    private const string DefaultRepository = "https://github.com/ozkancirak/Ration";

    public static string RepositoryUrl =>
        Environment.GetEnvironmentVariable("RATION_UPDATE_REPOSITORY") is { Length: > 0 } value
            ? value
            : DefaultRepository;

    public static string CurrentVersion
    {
        get
        {
            var version = typeof(AppUpdates).Assembly.GetName().Version;
            return version is null ? "0.2.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    /// <summary>
    /// Saklanan sonuç yeni bir sürüm bildiriyor mu. Güncellemeden sonra bayat kalan kayıt
    /// (bildirilen sürüm zaten çalışan sürüm) yeni sayılmaz.
    /// </summary>
    public static bool HasNewerUpdate(UpdateCheckResult? result) =>
        result is { UpdateAvailable: true, IsInstalled: true } &&
        UpdateService.IsNewer(result.AvailableVersion, CurrentVersion);

    public static UpdateService Service { get; } =
        new(new VelopackUpdateSource(RepositoryUrl), new RegistryUpdateStateStore());
}
