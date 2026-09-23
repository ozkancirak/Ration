using System.Runtime.CompilerServices;

namespace Ration.Tests;

/// <summary>Test çalıştırması tanı günlüğünü geçici bir dosyaya yazar, gerçek ration.log'a değil.</summary>
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void Redirect() =>
        Environment.SetEnvironmentVariable(
            "RATION_LOG_PATH",
            Path.Combine(Path.GetTempPath(), "ration-tests.log"));
}
