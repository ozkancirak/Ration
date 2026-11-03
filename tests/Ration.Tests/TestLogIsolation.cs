using System.Runtime.CompilerServices;

namespace Ration.Tests;

/// <summary>
/// The test run writes the diagnostic log to a temp file, not to the real ration.log.
/// The tests that verify text were written in Turkish; the language is pinned so that they
/// are independent of the machine's language.
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
