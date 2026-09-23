using System.Runtime.CompilerServices;

namespace Ration.Tests;

/// <summary>
/// Test çalıştırması tanı günlüğünü geçici bir dosyaya yazar, gerçek ration.log'a değil.
/// Metin doğrulayan testler Türkçe yazıldı; makinenin dilinden bağımsız olsun diye dil sabitlenir.
/// </summary>
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        Environment.SetEnvironmentVariable(
            "RATION_LOG_PATH",
            Path.Combine(Path.GetTempPath(), "ration-tests.log"));
        Ration.Core.L.Turkish = true;
    }
}
