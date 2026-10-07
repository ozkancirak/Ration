using System.Text;
using System.Text.RegularExpressions;

namespace Ration.Core.Diagnostics;

/// <summary>
/// Destek isterken yapıştırılacak tanı metni: sürüm, işletim sistemi ve son günlük satırları.
/// Günlük zaten e-posta ve gizli değerleri maskeler; burada kullanıcı klasörü yolları da silinir,
/// çünkü Windows kullanıcı adını taşır.
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
            // Aynı klasör hem ters hem düz eğik çizgiyle yazılmış olabilir.
            var pattern = string.Join(
                @"[\\/]+",
                userProfile.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            text = Regex.Replace(text, pattern, "%USERPROFILE%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Başka sürücüdeki ya da başka kullanıcıya ait profiller de kullanıcı adı taşır.
        return UsersFolder.Replace(text, @"<drive>:\Users\<user>");
    }
}
