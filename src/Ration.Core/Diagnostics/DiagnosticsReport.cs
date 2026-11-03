using System.Text;
using System.Text.RegularExpressions;

namespace Ration.Core.Diagnostics;

/// <summary>
/// Diagnostic text to paste when asking for support: version, operating system and the last log lines.
/// The log already masks emails and secret values; here the user folder paths are removed too,
/// because they carry the Windows user name.
/// </summary>
public static class DiagnosticsReport
{
    private static readonly Regex UsersFolder = new(
        @"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Build(
        string appVersion,
        string osDescription,
        string language,
        IEnumerable<string> logLines,
        string userProfile)
    {
        var report = new StringBuilder();
        report.AppendLine($"Ration {appVersion}");
        report.AppendLine($"OS: {osDescription}");
        report.AppendLine($"Language: {language}");
        report.AppendLine();
        report.AppendLine("--- Last log lines ---");
        foreach (var line in logLines)
        {
            report.AppendLine(RedactUserPaths(line, userProfile));
        }

        return report.ToString();
    }

    public static string RedactUserPaths(string text, string userProfile)
    {
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            // The same folder may be written with backslashes or forward slashes.
            var pattern = string.Join(
                @"[\\/]+",
                userProfile.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            text = Regex.Replace(text, pattern, "%USERPROFILE%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Profiles on other drives or belonging to other users also carry a user name.
        return UsersFolder.Replace(text, @"<drive>:\Users\<user>");
    }
}
