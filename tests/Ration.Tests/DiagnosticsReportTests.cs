using Ration.Core.Diagnostics;

namespace Ration.Tests;

public class DiagnosticsReportTests
{
    private const string Profile = @"C:\Users\sentetik";

    [Theory]
    [InlineData(@"read C:\Users\sentetik\.claude\.credentials.json", @"read %USERPROFILE%\.claude\.credentials.json")]
    [InlineData("read C:/Users/sentetik/.codex/auth.json", "read %USERPROFILE%/.codex/auth.json")]
    [InlineData(@"read c:\users\SENTETIK\x", @"read %USERPROFILE%\x")]
    [InlineData(@"other D:\Users\baskasi\file.txt", @"other <drive>:\Users\<user>\file.txt")]
    [InlineData("no path here", "no path here")]
    public void RedactUserPaths_RemovesTheUserName(string input, string expected)
    {
        Assert.Equal(expected, DiagnosticsReport.RedactUserPaths(input, Profile));
    }

    [Fact]
    public void Build_ContainsVersionAndRedactedLog()
    {
        var report = DiagnosticsReport.Build(
            "0.2.3", "Windows 11", "en", [@"log C:\Users\sentetik\x"], Profile);

        Assert.Contains("Ration 0.2.3", report);
        Assert.Contains("OS: Windows 11", report);
        Assert.Contains(@"log %USERPROFILE%\x", report);
        Assert.DoesNotContain("sentetik", report);
    }
}
