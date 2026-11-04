namespace Ration.Core.Providers;

/// <summary>
/// Where the providers' files live on disk.
///
/// CAUTION: the provider paths here are READ-ONLY.
/// These files are the user's own Claude Code / Codex CLI sessions;
/// writing, moving or locking them would sign the user out of their own CLI.
/// The only place Ration may write is <see cref="CacheDir"/>.
/// </summary>
public static class KnownPaths
{
    public static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // --- Claude (read-only) ---

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

    /// <summary>
    /// Possible local session logs of Claude Desktop. For read-only discovery only;
    /// ClaudeCostScanner does not scan them until the schema is verified.
    /// </summary>
    public static IReadOnlyList<string> ClaudeDesktopSessionRoots
    {
        get
        {
            var roots = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "local-agent-mode-sessions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions"),
            };

            var packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");
            try
            {
                roots.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                    .Select(path => Path.Combine(path, "LocalCache", "Roaming", "Claude")));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return roots;
        }
    }

    // --- Codex (read-only) ---

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

    // --- Antigravity (read-only) ---

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

    // --- OpenCode (read-only) ---

    public static string OpenCodeDataHome
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Home, ".local", "share")
                : overridden;
        }
    }

    public static string OpenCodeHome => Path.Combine(OpenCodeDataHome, "opencode");

    public static string OpenCodeAuthFile => Path.Combine(OpenCodeHome, "auth.json");

    public static string OpenCodeDatabaseFile
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("OPENCODE_DB");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(OpenCodeHome, "opencode.db")
                : overridden;
        }
    }

    public static string OpenCodeDatabaseCacheDir => Path.Combine(CacheDir, "cache", "opencode");

    // --- Cursor (read-only) ---

    /// <summary>The Cursor app's VS Code-style state database; the session token lives here.</summary>
    public static string CursorStateDatabaseFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    /// <summary>Cursor's Electron <c>Local State</c> file; the key of the encrypted token is here (DPAPI-protected).</summary>
    public static string CursorLocalStateFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "Local State");

    public static string ModelAliasesFile => Path.Combine(CacheDir, "model-aliases.json");

    public static string OpenCodeFreeModelsFile => Path.Combine(CacheDir, "free-models.json");

    public static string PricingOverridesFile => Path.Combine(CacheDir, "pricing-overrides.json");

    // --- GitHub Copilot CLI (read-only) ---

    /// <summary>The Copilot CLI's data root; COPILOT_HOME relocates it.</summary>
    public static string CopilotHome
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("COPILOT_HOME");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Home, ".copilot")
                : overridden;
        }
    }

    /// <summary>One folder per session, each with an events.jsonl; the usage totals are in its session.shutdown events.</summary>
    public static string CopilotSessionStateDir => Path.Combine(CopilotHome, "session-state");

    // --- Ration's own area (the only writable place) ---

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ration");
}
