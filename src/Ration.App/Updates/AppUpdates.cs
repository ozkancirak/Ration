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

    public static UpdateService Service { get; } =
        new(new VelopackUpdateSource(RepositoryUrl), new RegistryUpdateStateStore());
}
