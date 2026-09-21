namespace Kalan.Core.Providers;

/// <summary>
/// Sağlayıcı dosyalarının diskteki yerleri.
///
/// DİKKAT (AGENTS.md §2.1): buradaki sağlayıcı yolları SALT OKUNURDUR.
/// Bu dosyalar kullanıcının Claude Code / Codex CLI oturumlarının kendisidir;
/// yazmak, taşımak veya kilitlemek kullanıcıyı kendi CLI'ından düşürür.
/// Kalan'ın yazabileceği tek yer <see cref="CacheDir"/>.
/// </summary>
public static class KnownPaths
{
    public static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // --- Claude (salt okunur) ---

    public static string ClaudeHome
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Home, ".claude")
                : overridden;
        }
    }

    public static string ClaudeCredentialsFile => Path.Combine(ClaudeHome, ".credentials.json");

    public static string ClaudeProjectsDir => Path.Combine(ClaudeHome, "projects");

    // --- Codex (salt okunur) ---

    public static string CodexHome
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Home, ".codex")
                : overridden;
        }
    }

    public static string CodexAuthFile => Path.Combine(CodexHome, "auth.json");

    public static string CodexSessionsDir => Path.Combine(CodexHome, "sessions");

    // --- Antigravity (salt okunur) ---

    public static string DefaultGeminiCliHome => Path.Combine(Home, ".gemini");

    public static string? GeminiCliHomeOverride
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
            return string.IsNullOrWhiteSpace(overridden) ? null : overridden;
        }
    }

    public static string AntigravityDefaultCliLog => Path.Combine(
        DefaultGeminiCliHome,
        "antigravity-cli",
        "cli.log");

    public static string? AntigravityOverrideCliLog =>
        GeminiCliHomeOverride is { } root
            ? Path.Combine(root, "antigravity-cli", "cli.log")
            : null;

    // --- Kalan'ın kendi alanı (yazılabilir tek yer) ---

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kalan");
}
