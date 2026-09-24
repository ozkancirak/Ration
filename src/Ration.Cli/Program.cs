using System.Text.Json;
using System.Text.RegularExpressions;
using Ration.Core;
using Ration.Core.Abstractions;
using Ration.Core.Cost;
using Ration.Core.Diagnostics;
using Ration.Core.Discovery;
using Ration.Core.Model;
using Ration.Core.Providers;
using Ration.Core.Providers.Antigravity;
using Ration.Core.Providers.Claude;
using Ration.Core.Providers.Codex;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Ration.Platform.Windows.Theme;
using Ration.Platform.Windows.Tray;
using Ration.Platform.Windows.Providers;
using AppProcess = System.Diagnostics.Process;
using AppProcessStartInfo = System.Diagnostics.ProcessStartInfo;

// Ration CLI — UI olmadan doğrulamanın birincil aracı.
// Token değerleri hiçbir çıktıda gösterilmez.

// Türkçe karakterler konsolun kod sayfasına göre bozuluyordu; çıktı her yerde UTF-8.
Console.OutputEncoding = System.Text.Encoding.UTF8;
L.ApplyCulture();

Ration.Core.LegacySettingsMigration.Run();
Ration.Platform.Windows.App.StartupRegistration.MigrateLegacyEntry();

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Ration/0.1");

var providers = ProviderRegistry.CreateAll(
        http,
        AntigravityProcessPortFinder.FindPorts,
        AntigravityProcessPortFinder.FindEndpoints)
    .ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

if (HasFlag(args, "--log"))
{
    PrintLog();
    return 0;
}

if (HasFlag(args, "--selftest"))
{
    return RunSelfTest(args);
}

if (args.Length >= 2 &&
    args[0].Equals("--raw", StringComparison.OrdinalIgnoreCase) &&
    !args[1].StartsWith('-'))
{
    return await RunUsageAsync(args[1], asJson: false, showRaw: true);
}

var command = args[0].ToLowerInvariant();
var wantsJson = HasFlag(args, "--json");
var wantsRaw = HasFlag(args, "--raw");
var target = GetOption(args, "-p") ?? GetOption(args, "--provider") ?? "all";

// Sağlayıcı keşfi: bayrak-önce çağrı (ration --discover gemini). Ağ yok,
// provider kodu üretmez; yalnızca yol + şema (değer asla).
if (HasFlag(args, "--discover"))
{
    var which = GetOption(args, "-p") ?? GetOption(args, "--provider")
        ?? args.FirstOrDefault(a => !a.StartsWith('-') && !a.Equals("--discover", StringComparison.OrdinalIgnoreCase))
        ?? "all";
    return await RunDiscoverAsync(which, wantsJson);
}

switch (command)
{
    case "usage":
        return await RunUsageAsync(target, wantsJson, wantsRaw);

    case "cost":
        return RunCost(target, wantsJson, HasFlag(args, "--schema"), GetOption(args, "--days"));

    case "theme":
        PrintThemeInfo();
        return 0;

    case "icon-preview":
    case "render-icons":
        RenderIconPreview(GetOption(args, "--out") ?? "contact-sheet.png");
        return 0;

    case "diagnose":
        PrintThemeInfo();
        PrintDiagnostics();
        return await RunUsageAsync("all", asJson: false, showRaw: wantsRaw);

    default:
        Console.Error.WriteLine(L.T($"Unknown command: {command}", $"Bilinmeyen komut: {command}"));
        PrintHelp();
        return 2;
}

async Task<int> RunUsageAsync(string which, bool asJson, bool showRaw)
{
    List<IUsageProvider> selected;

    if (which.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        selected = providers.Values.ToList();
    }
    else if (providers.TryGetValue(which, out var single))
    {
        selected = new List<IUsageProvider> { single };
    }
    else
    {
        Console.Error.WriteLine(L.T($"Unknown provider: {which}. Options: claude, codex, antigravity, opencode, all", $"Bilinmeyen sağlayıcı: {which}. Seçenekler: claude, codex, antigravity, opencode, all"));
        return 2;
    }

    var snapshots = new List<UsageSnapshot>();
    foreach (var provider in selected)
    {
        snapshots.Add(await ProviderResolver.ResolveAsync(provider));
    }

    if (showRaw) PrintRawResponses(selected);

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            snapshots,
            new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        foreach (var snapshot in snapshots) PrintSnapshot(snapshot);
    }

    return snapshots.All(s => s.Status == ProviderStatus.Ok) ? 0 : 1;
}

void PrintSnapshot(UsageSnapshot snapshot)
{
    var via = snapshot.ResolvedVia is null ? string.Empty : $"  ({snapshot.ResolvedVia})";
    var plan = string.IsNullOrWhiteSpace(snapshot.PlanName) ? string.Empty : $"  plan: {snapshot.PlanName}";
    Console.WriteLine($"{snapshot.ProviderId}  [{snapshot.Status}]{via}{plan}");

    if (snapshot.StaleReason is not null)
    {
        Console.WriteLine($"  ! {snapshot.StaleReason}");
    }

    foreach (var window in snapshot.Windows)
    {
        var label = window.Label ?? window.Kind.ToString();
        var reset = window.ResetsAt is { } resetsAt
            ? L.T($"   resets: {resetsAt.ToLocalTime():g}", $"   sıfırlanma: {resetsAt.ToLocalTime():dd.MM HH:mm}")
            : string.Empty;

        Console.WriteLine($"  {label,-20} " + L.T($"{window.Percent,5:F1}%", $"%{window.Percent,5:F1}") + reset);
    }

    if (snapshot.Credits is { } credits)
    {
        Console.WriteLine($"  {L.T("Credits", "Kredi"),-20} {credits.RemainingCredits}");
    }

    if (snapshot.Cost is { } cost)
    {
        Console.WriteLine($"  {L.T("Input tokens", "Girdi token"),-20} {cost.InputTokens}");
        Console.WriteLine($"  {L.T("Output tokens", "Çıktı token"),-20} {cost.OutputTokens}");
        Console.WriteLine($"  {L.T("Reasoning", "Akıl yürütme"),-20} {cost.ReasoningTokens}");
        Console.WriteLine($"  {L.T("Cache read", "Cache okuma"),-20} {cost.CacheReadTokens}");
        Console.WriteLine($"  {L.T("Cache write", "Cache yazma"),-20} {cost.CacheCreationTokens}");
    }

    if (snapshot.Windows.Count == 0)
    {
        Console.WriteLine(L.T("  (no window data)", "  (pencere verisi yok)"));
    }

    Console.WriteLine();
}

void PrintRawResponses(List<IUsageProvider> selected)
{
    foreach (var provider in selected)
    {
        foreach (var source in provider.Sources)
        {
            if (source is ClaudeOAuthUsageSource claude)
            {
                if (claude.LastRawResponse is not null)
                {
                    Console.WriteLine(L.T($"--- raw response: {provider.Id} ({source.Kind}) — identity fields redacted ---", $"--- ham yanıt: {provider.Id} ({source.Kind}) — kimlik alanları gizlendi ---"));
                    Console.WriteLine(RawResponseRedactor.Redact(claude.LastRawResponse));
                    Console.WriteLine();
                }

                PrintClaudeDiagnostics(claude);
                continue;
            }

            var raw = source is CodexOAuthUsageSource codex
                ? codex.LastRawResponse
                : null;

            if (raw is null) continue;

            // Kota uç noktaları token döndürmez ama e-posta ve hesap kimliği döndürür.
            // Bu çıktı bir issue'ya ya da sohbete yapıştırılacak; maskelenmeden gösterilmez.
            Console.WriteLine(L.T($"--- raw response: {provider.Id} ({source.Kind}) — identity fields redacted ---", $"--- ham yanıt: {provider.Id} ({source.Kind}) — kimlik alanları gizlendi ---"));
            Console.WriteLine(RawResponseRedactor.Redact(raw));
            Console.WriteLine();
        }
    }
}

void PrintClaudeDiagnostics(ClaudeOAuthUsageSource source)
{
    static string YesNo(bool? value) => value switch
    {
        true => L.T("yes", "evet"),
        false => L.T("no", "hayır"),
        _ => "—",
    };

    Console.WriteLine(L.T("--- Claude diagnostics (credential values redacted) ---", "--- Claude tanı (kimlik değerleri gizlendi) ---"));
    Console.WriteLine(L.T($".credentials.json readable: {YesNo(source.LastCredentialsAvailable)}", $".credentials.json okunabildi: {YesNo(source.LastCredentialsAvailable)}"));
    Console.WriteLine($"expiresAt: {(source.LastCredentialsExpiresAt?.ToUniversalTime().ToString("O") ?? L.T("none", "yok"))}");
    Console.WriteLine(L.T($"expiresAt passed: {YesNo(source.LastCredentialsAvailable ? source.LastCredentialsExpired : null)}", $"expiresAt geçmiş: {YesNo(source.LastCredentialsAvailable ? source.LastCredentialsExpired : null)}"));
    Console.WriteLine(L.T($"refreshToken present: {YesNo(source.LastCredentialsAvailable ? source.LastCredentialsHasRefreshToken : null)}", $"refreshToken mevcut: {YesNo(source.LastCredentialsAvailable ? source.LastCredentialsHasRefreshToken : null)}"));
    Console.WriteLine(L.T("HTTP status: ", "HTTP kodu: ") + (source.LastStatusCode?.ToString() ?? L.T("no request made", "istek yapılmadı")));
    Console.WriteLine($"Retry-After: {source.LastRetryAfter ?? L.T("none", "yok")}");
    Console.WriteLine(L.T("request URL", "istek URL") + $": GET {ClaudeOAuthUsageSource.UsageEndpoint}");
    Console.WriteLine(L.T("header names: Authorization, anthropic-beta", "header adları: Authorization, anthropic-beta"));

    Console.WriteLine();
}

int RunCost(string which, bool asJson, bool schemaOnly, string? daysOption)
{
    if (schemaOnly)
    {
        PrintSchema();
        return 0;
    }

    var days = 30;
    if (daysOption is not null && int.TryParse(daysOption, out var parsedDays) && parsedDays > 0)
    {
        days = parsedDays;
    }

    var since = DateTimeOffset.UtcNow.AddDays(-days);
    var pricing = PricingTable.LoadOrEmpty();
    var normalized = which.ToLowerInvariant();

    var results = new List<(string Provider, CostScanResult Scan, CostReport Report)>();

    if (normalized is "all" or "claude")
    {
        var scan = ClaudeCostScanner.Scan(since);
        results.Add(("claude", scan, CostEstimator.Estimate(scan, pricing)));
    }

    if (normalized is "all" or "codex")
    {
        var scan = CodexCostScanner.Scan(since);
        results.Add(("codex", scan, CostEstimator.Estimate(scan, pricing)));
    }

    if (results.Count == 0)
    {
        Console.Error.WriteLine(L.T($"Unknown provider: {which}. Options: claude, codex, antigravity, opencode, all", $"Bilinmeyen sağlayıcı: {which}. Seçenekler: claude, codex, antigravity, opencode, all"));
        return 2;
    }

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            results.Select(r => new { provider = r.Provider, report = r.Report, models = r.Scan.Tally.Models }),
            new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    if (pricing.IsEmpty)
    {
        Console.WriteLine(L.T("Pricing table is empty — showing token counts only.", "Fiyat tablosu boş — yalnızca token sayıları gösteriliyor."));
        Console.WriteLine(L.T($"To add prices: {PricingTable.DefaultPath}", $"Fiyat eklemek için: {PricingTable.DefaultPath}"));
        Console.WriteLine();
    }

    foreach (var (provider, scan, report) in results)
    {
        PrintCost(provider, scan, report, pricing);
    }

    return 0;
}

void PrintCost(string provider, CostScanResult scan, CostReport report, PricingTable pricing)
{
    Console.WriteLine(
        $"{provider}   {scan.PeriodStart.ToLocalTime():dd.MM} → {scan.PeriodEnd.ToLocalTime():dd.MM}   ({scan.FilesScanned} " + L.T("files", "dosya") + ")");

    if (scan.Note is not null) Console.WriteLine($"  ! {scan.Note}");

    if (scan.Tally.IsEmpty)
    {
        Console.WriteLine(L.T("  (no token data)", "  (token verisi yok)"));
        Console.WriteLine();
        return;
    }

    foreach (var model in scan.Tally.Models)
    {
        var missingPrice = pricing.Find(model.Model) is null ? L.T("  (no price)", "  (fiyat yok)") : string.Empty;
        Console.WriteLine($"  {model.Model,-32} {model.TotalTokens,14:N0} token{missingPrice}");
    }

    Console.WriteLine($"  {L.T("input / output", "giriş / çıkış"),-32} {report.InputTokens,14:N0} / {report.OutputTokens:N0}");
    Console.WriteLine($"  {L.T("cache read / write", "önbellek okuma / yazma"),-32} {report.CacheReadTokens,14:N0} / {report.CacheCreationTokens:N0}");

    if (report.ReasoningTokens > 0)
    {
        Console.WriteLine($"  {L.T("reasoning (within output)", "akıl yürütme (çıkış içinde)"),-32} {report.ReasoningTokens,14:N0}");
    }

    if (!pricing.IsEmpty)
    {
        Console.WriteLine($"  {L.T("estimated cost", "tahmini maliyet"),-32} {report.TotalCost,14:N4} {report.Currency}");

        if (report.ModelsWithoutPricing is { Count: > 0 } missing)
        {
            Console.WriteLine(L.T($"  ! Model(s) without a price are not included in the cost: {string.Join(", ", missing)}", $"  ! Fiyatı bilinmeyen model(ler) maliyete dahil değil: {string.Join(", ", missing)}"));
        }
    }

    Console.WriteLine();
}

void PrintSchema()
{
    Console.WriteLine(L.T("JSONL schema discovery — only key paths and types are printed, never VALUES.", "JSONL şema keşfi — yalnızca anahtar yolları ve türler yazılır, DEĞER yazılmaz."));
    Console.WriteLine();

    Console.WriteLine(L.T($"Codex — lines containing token_count ({KnownPaths.CodexSessionsDir}):", $"Codex — token_count içeren satırlar ({KnownPaths.CodexSessionsDir}):"));
    foreach (var path in JsonlSchemaProbe.DescribeKeyPaths(KnownPaths.CodexSessionsDir, "token_count"))
    {
        Console.WriteLine($"  {path}");
    }

    Console.WriteLine();
    Console.WriteLine(L.T($"Claude — lines containing usage ({KnownPaths.ClaudeProjectsDir}):", $"Claude — usage içeren satırlar ({KnownPaths.ClaudeProjectsDir}):"));
    foreach (var path in JsonlSchemaProbe.DescribeKeyPaths(KnownPaths.ClaudeProjectsDir, "\"usage\""))
    {
        Console.WriteLine($"  {path}");
    }

    Console.WriteLine();
}

void PrintDiagnostics()
{
    Console.WriteLine("Ration diagnose");
    Console.WriteLine();
    Console.WriteLine(L.T("Paths (all read-only):", "Yollar (hepsi salt okunur):"));
    PrintPath(L.T("Claude credentials", "Claude kimlik"), KnownPaths.ClaudeCredentialsFile, isFile: true);
    PrintPath("Claude projects", KnownPaths.ClaudeProjectsDir, isFile: false);
    PrintPath("Codex auth", KnownPaths.CodexAuthFile, isFile: true);
    PrintPath("Codex sessions", KnownPaths.CodexSessionsDir, isFile: false);
    Console.WriteLine();
    Console.WriteLine(L.T("Credential readability:", "Kimlik okunabilirliği:"));
    Console.WriteLine($"  {"Claude",-16} {(ClaudeCredentialStore.TryRead() is not null ? L.T("read", "okundu") : L.T("unreadable", "okunamadı"))}");
    Console.WriteLine($"  {"Codex",-16} {(CodexCredentialStore.TryRead() is not null ? L.T("read", "okundu") : L.T("unreadable", "okunamadı"))}");
    Console.WriteLine();
    Console.WriteLine(L.T("Note: token values are never shown in any output.", "Not: token değerleri hiçbir çıktıda gösterilmez."));
    Console.WriteLine();
}

void PrintLog()
{
    var lines = Trace.ReadLastLines(100);
    if (lines.Count == 0)
    {
        Console.WriteLine(L.T($"No log: {Trace.LogPath}", $"Günlük yok: {Trace.LogPath}"));
        return;
    }

    foreach (var line in lines)
    {
        Console.WriteLine(line);
    }
}

void PrintPath(string label, string path, bool isFile)
{
    var exists = isFile ? File.Exists(path) : Directory.Exists(path);
    Console.WriteLine($"  {label,-16} {(exists ? L.T("yes", "var") : L.T("no", "yok")),-5} {path}");
}

void PrintThemeInfo()
{
    Console.WriteLine(L.T("=== Windows theme and system colors ===", "=== Windows Tema ve Sistem Renkleri ==="));
    Console.WriteLine(L.T("  Taskbar theme       : ", "  Görev Çubuğu Teması : ") + (WindowsThemeListener.IsTaskbarLightTheme() ? L.T("Light", "Açık (Light)") : L.T("Dark", "Koyu (Dark)")));
    Console.WriteLine(L.T("  App theme           : ", "  Uygulama Teması     : ") + (WindowsThemeListener.IsAppLightTheme() ? L.T("Light", "Açık (Light)") : L.T("Dark", "Koyu (Dark)")));
    Console.WriteLine(L.T("  High contrast       : ", "  Yüksek Kontrast     : ") + (SystemAccent.IsHighContrast ? L.T("On", "Aktif") : L.T("Off", "Kapalı")));

    var accent = SystemAccent.GetAccent();
    var light1 = SystemAccent.GetAccentLight1();
    var dark1 = SystemAccent.GetAccentDark1();

    Console.WriteLine($"  Accent (main)       : #{accent.R:X2}{accent.G:X2}{accent.B:X2} (R:{accent.R} G:{accent.G} B:{accent.B})");
    Console.WriteLine($"  Accent (Light1)     : #{light1.R:X2}{light1.G:X2}{light1.B:X2} (R:{light1.R} G:{light1.G} B:{light1.B})");
    Console.WriteLine($"  Accent (Dark1)      : #{dark1.R:X2}{dark1.G:X2}{dark1.B:X2} (R:{dark1.R} G:{dark1.G} B:{dark1.B})");
    Console.WriteLine();
}

int RunSelfTest(string[] forwardedArgs)
{
    var executable = FindAppExecutable();
    if (executable is null)
    {
        Console.Error.WriteLine(L.T("Ration.exe not found; build the Ration.App project first.", "Ration.exe bulunamadı; önce Ration.App projesini derleyin."));
        return 2;
    }

    try
    {
        var startInfo = new AppProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in forwardedArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = AppProcess.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine(L.T("Could not start the self-test process.", "Self-test süreci başlatılamadı."));
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(L.T($"Could not start self-test: {ex.GetType().Name}", $"Self-test başlatılamadı: {ex.GetType().Name}"));
        return 1;
    }
}

string? FindAppExecutable()
{
    var candidates = new List<string>
    {
        Path.Combine(AppContext.BaseDirectory, "Ration.exe"),
    };

    // Kaynak ağaçta CLI ve App ayrı projeler olduğundan Debug/Release çıktılarını
    // sınırlı, deterministik adaylar olarak kontrol et; tüm diski tarama.
    string sourceRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    foreach (var configuration in new[] { "Debug", "Release" })
    {
        candidates.Add(Path.Combine(
            sourceRoot,
            "Ration.App",
            "bin",
            configuration,
            "net10.0-windows10.0.26100.0",
            "win-x64",
            "Ration.exe"));
    }

    return candidates.FirstOrDefault(path => File.Exists(path)
        && !string.Equals(Path.GetFullPath(path), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
}

void RenderIconPreview(string outputPath)
{
    var percentages = new double?[] { null, 0.0, 50.0, 75.0, 90.0, 100.0 };
    var sizes = new[] { 16, 20, 24, 32 };
    int width = 940;
    int height = 760;

    using var canvas = new Bitmap(width, height);
    using (var g = Graphics.FromImage(canvas))
    {
        g.Clear(Color.FromArgb(24, 24, 24));

        using var fontHeader = new Font("Segoe UI", 12, FontStyle.Bold);
        using var fontTitle = new Font("Segoe UI", 10, FontStyle.Bold);
        using var fontLabel = new Font("Segoe UI", 9, FontStyle.Regular);
        using var fontSubLabel = new Font("Segoe UI", 8, FontStyle.Regular);
        using var whiteBrush = new SolidBrush(Color.White);
        using var grayBrush = new SolidBrush(Color.FromArgb(170, 170, 170));

        g.DrawString("Ration — Tray icon contact sheet", fontHeader, whiteBrush, 24, 16);
        g.DrawString("Vertical outline tank/gauge · DPI × theme × usage · — = no data · fill = remaining quota", fontSubLabel, grayBrush, 24, 40);

        var themes = new[]
        {
            (IsLight: false, Name: "Dark taskbar (Windows 11 default)", Bg: Color.FromArgb(32, 32, 32), Fg: Color.White, LabelFg: Color.FromArgb(200, 200, 200), Y: 68),
            (IsLight: true, Name: "Light taskbar", Bg: Color.FromArgb(243, 243, 243), Fg: Color.Black, LabelFg: Color.FromArgb(60, 60, 60), Y: 410)
        };

        foreach (var theme in themes)
        {
            g.DrawString(theme.Name, fontTitle, whiteBrush, 24, theme.Y);

            using var bgBrush = new SolidBrush(theme.Bg);
            g.FillRectangle(bgBrush, 24, theme.Y + 24, 892, 300);

            using var themeFgBrush = new SolidBrush(theme.Fg);
            using var themeLabelBrush = new SolidBrush(theme.LabelFg);

            // Kolon başlıkları (yüzdeler)
            int colStartX = 140;
            int colWidth = 122;
            for (int p = 0; p < percentages.Length; p++)
            {
                var pct = percentages[p];
                string thresholdText = pct is null
                    ? "No data"
                    : pct >= 90
                        ? "Critical"
                        : "Monochrome";
                string percentLabel = pct is double value ? $"{value:F0}%" : "—";

                int cx = colStartX + (p * colWidth);
                g.DrawString(percentLabel, fontTitle, themeFgBrush, cx + 20, theme.Y + 30);
                g.DrawString(thresholdText, fontSubLabel, themeLabelBrush, cx + 18, theme.Y + 48);
            }

            // Satır başlıkları (boyutlar) ve ikon çizimleri
            int rowStartY = theme.Y + 70;
            for (int s = 0; s < sizes.Length; s++)
            {
                int size = sizes[s];
                int ry = rowStartY + (s * 58);

                g.DrawString($"{size}px", fontTitle, themeFgBrush, 40, ry + 12);
                string dpiLabel = size switch
                {
                    16 => "100%",
                    20 => "125%",
                    24 => "150%",
                    32 => "200%",
                    _ => ""
                };
                g.DrawString(dpiLabel, fontSubLabel, themeLabelBrush, 40, ry + 28);

                for (int p = 0; p < percentages.Length; p++)
                {
                    var pct = percentages[p];
                    int cx = colStartX + (p * colWidth);

                    using var icon = TrayIconRenderer.CreateGaugeBitmap(pct, theme.IsLight, size);

                    // 1x Gerçek boyut (PixelOffsetMode.None, NearestNeighbor)
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.None;
                    g.SmoothingMode = SmoothingMode.None;

                    int iconY = ry + (48 - size) / 2;
                    g.DrawImage(icon, cx + 12, iconY, size, size);

                    // Büyütülmüş görünüm (36px boyutunda)
                    int previewSize = 36;
                    int previewY = ry + (48 - previewSize) / 2;
                    g.DrawImage(icon, cx + 55, previewY, previewSize, previewSize);
                }
            }
        }
    }

    var dir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }
    canvas.Save(outputPath, ImageFormat.Png);
    Console.WriteLine($"Icon contact sheet saved: {outputPath}");
}

async Task<int> RunDiscoverAsync(string which, bool asJson)
{
    var normalized = which.ToLowerInvariant();
    if (normalized == "claude-scope")
    {
        return RunClaudeScopeDiscover();
    }

    if (normalized == "antigravity")
    {
        return await RunAntigravityDiscoverAsync();
    }

    var selected = normalized == "all"
        ? new[] { "gemini", "copilot" }
        : new[] { normalized };

    foreach (var name in selected)
    {
        if (ProviderDiscovery.RootsFor(name) is null)
        {
            Console.Error.WriteLine(L.T($"Unknown provider: {name}. Options: gemini, copilot, antigravity, all", $"Bilinmeyen sağlayıcı: {name}. Seçenekler: gemini, copilot, antigravity, all"));
            return 2;
        }
    }

    var reports = selected.Select(name => ProviderDiscovery.Discover(name)).ToList();

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    Console.WriteLine("Provider discovery — only paths, key paths and types are printed, never VALUES.");
    Console.WriteLine();

    foreach (var report in reports)
    {
        Console.WriteLine($"{report.Provider}   ({string.Join(", ", report.Roots)})");

        foreach (var note in report.Notes) Console.WriteLine($"  ! {note}");

        if (report.Files.Count == 0) Console.WriteLine("  (no files)");

        foreach (var file in report.Files)
        {
            Console.WriteLine($"  {file.Path}   ({file.Size:N0} bytes)");

            if (file.Schema is { Count: > 0 } schema)
            {
                foreach (var path in schema) Console.WriteLine($"    {path}");
            }
        }

        Console.WriteLine();
    }

    return 0;
}

int RunClaudeScopeDiscover()
{
    Console.WriteLine("Claude scope discovery — only paths, counts and key paths are printed; never VALUES.");
    Console.WriteLine();

    foreach (var root in KnownPaths.ClaudeDesktopSessionRoots)
    {
        IReadOnlyList<string> files;
        try
        {
            files = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch (IOException)
        {
            files = Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            files = Array.Empty<string>();
        }

        Console.WriteLine($"{root}  dir={(Directory.Exists(root) ? "yes" : "no")}  files={files.Count}");
        if (files.Count == 0) continue;

        var first = files[0];
        var schema = JsonlSchemaProbe.DescribeKeyPaths(root, maxFiles: 1);
        var usagePaths = schema
            .Where(path => path.StartsWith("message.usage.", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var modelPaths = schema
            .Where(path => path.Contains("model", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Console.WriteLine($"  first-file={first}");
        Console.WriteLine($"  message.usage.*={(usagePaths.Length > 0 ? "yes" : "no")}");
        Console.WriteLine($"  model fields={(modelPaths.Length > 0 ? string.Join(", ", modelPaths) : "none")}");
        Console.WriteLine("  key-paths:");
        foreach (var path in schema) Console.WriteLine($"    {path}");
    }

    Console.WriteLine();
    Console.WriteLine("Without a verified schema in Desktop logs, Claude scope stays Claude Code only.");
    return 0;
}

async Task<int> RunAntigravityDiscoverAsync()
{
    const string endpointPath =
        "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    const string httpsErrorText = "Client sent an HTTP request to an HTTPS server";

    var processes = AntigravityProcessPortFinder.FindCandidateProcesses();
    var listeners = AntigravityProcessPortFinder.FindCandidateListeners();
    IReadOnlyList<AntigravityProcessEndpoint> endpoints;
    try
    {
        endpoints = AntigravityProcessPortFinder.FindEndpoints();
    }
    catch
    {
        endpoints = Array.Empty<AntigravityProcessEndpoint>();
    }

    var csrfByPort = endpoints
        .GroupBy(endpoint => endpoint.Port)
        .ToDictionary(
            group => group.Key,
            group => group.Select(endpoint => endpoint.CsrfToken)
                .FirstOrDefault(token => !string.IsNullOrWhiteSpace(token)));

    Console.WriteLine("Antigravity discovery");
    Console.WriteLine();
    Console.WriteLine("Candidate processes:");
    if (processes.Count == 0)
    {
        Console.WriteLine("  (none)");
    }
    else
    {
        foreach (var process in processes)
        {
            Console.WriteLine(
                $"  - {process.Name}.exe  PID={process.ProcessId}  command line={process.CommandLine}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Listening 127.0.0.1 ports:");
    if (listeners.Count == 0)
    {
        Console.WriteLine("  (none)");
    }
    else
    {
        foreach (var listener in listeners)
        {
            Console.WriteLine($"  - PID={listener.ProcessId}  port={listener.Port}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("RetrieveUserQuotaSummary probe:");
    if (listeners.Count == 0)
    {
        Console.WriteLine("  (none)");
    }
    else
    {
        using var probeHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
        };
        using var probeHttp = new HttpClient(probeHandler) { Timeout = TimeSpan.FromSeconds(2) };
        probeHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Ration/0.1 discover");

        foreach (var listener in listeners
                     .OrderBy(listener => listener.ProcessId)
                     .ThenByDescending(listener => listener.Port))
        {
            var probe = await ProbeAntigravityPortAsync(
                probeHttp,
                listener.Port,
                csrfByPort.GetValueOrDefault(listener.Port));

            if (probe.Failure is not null)
            {
                Console.WriteLine(
                    $"  - PID={listener.ProcessId} port={listener.Port} HTTP={probe.Failure} body=(none)");
                continue;
            }

            var shape = DescribeJsonShape(probe.Body);
            Console.WriteLine(
                $"  - PID={listener.ProcessId} port={listener.Port} HTTP={probe.StatusCode} transport={probe.Scheme} body={shape}");

            if (probe.StatusCode == 200)
            {
                PrintAntigravityGroupNames(probe.Body);
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("cli.log:");
    PrintAntigravityLogDiagnostics("default", KnownPaths.AntigravityDefaultCliLog);
    if (KnownPaths.AntigravityOverrideCliLog is { } overrideLog &&
        !string.Equals(
            KnownPaths.AntigravityDefaultCliLog,
            overrideLog,
            StringComparison.OrdinalIgnoreCase))
    {
        PrintAntigravityLogDiagnostics("GEMINI_CLI_HOME", overrideLog);
    }

    return 0;

    async Task<(int? StatusCode, string Body, string Scheme, string? Failure)> ProbeAntigravityPortAsync(
        HttpClient client,
        int port,
        string? csrfToken)
    {
        try
        {
            foreach (var scheme in new[] { "http", "https" })
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{scheme}://127.0.0.1:{port}{endpointPath}");
                request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                if (!string.IsNullOrWhiteSpace(csrfToken))
                {
                    request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);
                }

                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead);
                var body = await response.Content.ReadAsStringAsync();

                if (scheme == "http" &&
                    body.Contains(httpsErrorText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return ((int)response.StatusCode, body, scheme, null);
            }

            return (null, string.Empty, "https", "connection-refused");
        }
        catch (HttpRequestException)
        {
            return (null, string.Empty, "http", "connection-refused");
        }
        catch (TaskCanceledException)
        {
            return (null, string.Empty, "http", "timeout");
        }
    }
}

void PrintAntigravityLogDiagnostics(string label, string path)
{
    Console.WriteLine($"  {label}: {(File.Exists(path) ? "yes" : "no")}");
    foreach (var line in AntigravityPortFinder.ReadLastListeningLines(path))
    {
        var port = AntigravityPortFinder.FindPortInLogText(line);
        Console.WriteLine($"    listening port={(port?.ToString() ?? "unknown")}");
    }
}

static string DescribeJsonShape(string body)
{
    if (string.IsNullOrWhiteSpace(body)) return "empty";

    try
    {
        using var document = JsonDocument.Parse(body);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        CollectJsonPaths(document.RootElement, string.Empty, paths);

        if (paths.Count == 0) return document.RootElement.ValueKind.ToString().ToLowerInvariant();
        return string.Join(", ", paths.OrderBy(path => path).Take(80));
    }
    catch (JsonException)
    {
        return "non-json";
    }
}

static void CollectJsonPaths(
    JsonElement element,
    string path,
    HashSet<string> paths)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            var childPath = string.IsNullOrEmpty(path)
                ? property.Name
                : $"{path}.{property.Name}";
            paths.Add(childPath);
            CollectJsonPaths(property.Value, childPath, paths);
        }

        return;
    }

    if (element.ValueKind == JsonValueKind.Array)
    {
        var arrayPath = path.EndsWith("[]", StringComparison.Ordinal) ? path : path + "[]";
        foreach (var item in element.EnumerateArray())
        {
            CollectJsonPaths(item, arrayPath, paths);
        }
    }
}

static void PrintAntigravityGroupNames(string body)
{
    try
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var groups = root.TryGetProperty("groups", out var directGroups) &&
                     directGroups.ValueKind == JsonValueKind.Array
            ? directGroups
            : root.TryGetProperty("response", out var response) &&
              response.ValueKind == JsonValueKind.Object &&
              response.TryGetProperty("groups", out var nestedGroups) &&
              nestedGroups.ValueKind == JsonValueKind.Array
                ? nestedGroups
                : default;

        if (groups.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("    200 group/bucket names: (no groups array)");
            return;
        }

        foreach (var group in groups.EnumerateArray())
        {
            var groupName = group.TryGetProperty("displayName", out var displayName)
                ? SafeDiagnosticName(displayName.GetString())
                : "(unnamed group)";
            Console.WriteLine($"    group={groupName}");

            if (!group.TryGetProperty("buckets", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine("      bucket=(none)");
                continue;
            }

            foreach (var bucket in buckets.EnumerateArray())
            {
                var bucketName = bucket.TryGetProperty("displayName", out var bucketDisplayName)
                    ? SafeDiagnosticName(bucketDisplayName.GetString())
                    : "(unnamed bucket)";
                Console.WriteLine($"      bucket={bucketName}");
            }
        }
    }
    catch (JsonException)
    {
        Console.WriteLine("    200 group/bucket names: (JSON could not be parsed)");
    }
}

static string SafeDiagnosticName(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "(empty)";

    var safe = Regex.Replace(value, @"(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", "[email]");
    safe = Regex.Replace(
        safe,
        @"(?i)\b(?:bearer|token|api[_-]?key|csrf[_-]?token)\s*[:=]\s*\S+",
        "[redacted]");
    safe = Regex.Replace(
        safe,
        @"\b[0-9a-f]{8}-[0-9a-f-]{27,}\b",
        "[id]",
        RegexOptions.IgnoreCase);

    return safe.Length <= 120 ? safe : safe[..120] + "…";
}

void PrintHelp()
{
    Console.WriteLine(L.T("Ration — AI quota monitor", "Ration — AI kota göstergesi"));
    Console.WriteLine();
    Console.WriteLine(L.T("Usage:", "Kullanım:"));
    Console.WriteLine("  ration usage [-p claude|codex|antigravity|opencode|all] [--json] [--raw]");
    Console.WriteLine("  ration cost  [-p claude|codex|all] [--days N] [--json]");
    Console.WriteLine("  ration --discover [gemini|copilot|antigravity|claude-scope|all] [--json]  " + L.T("# discovery/schema; never prints values", "# keşif/şema; değer yazmaz"));
    Console.WriteLine("  ration icon-preview [--out contact-sheet.png]  " + L.T("# renders the DPI/theme contact sheet", "# DPI/tema temas levhası üretir"));
    Console.WriteLine("  ration diagnose [--raw]");
    Console.WriteLine("  ration --log                         " + L.T("# last 100 lines of ration.log", "# ration.log son 100 satır"));
    Console.WriteLine("  ration --selftest [--screenshot-dir DIR]  " + L.T("# real menu input test", "# gerçek menü girdisi testi"));
    Console.WriteLine();
    Console.WriteLine(L.T("Examples:", "Örnekler:"));
    Console.WriteLine("  ration usage -p claude");
    Console.WriteLine("  ration usage -p all --json");
    Console.WriteLine("  ration usage -p claude --raw     " + L.T("# shows the endpoint's raw schema", "# uç noktanın ham şemasını gösterir"));
    Console.WriteLine("  ration --raw claude               " + L.T("# Claude HTTP diagnostics + redacted response", "# Claude HTTP tanısı + redakte yanıt"));
    Console.WriteLine("  ration icon-preview --out contact-sheet.png");
    Console.WriteLine("  ration --log");
    Console.WriteLine("  ration --selftest --screenshot-dir .\\selftest");
    Console.WriteLine();
    Console.WriteLine(L.T("Exit code: 0 if all providers are Ok, otherwise 1.", "Çıkış kodu: tüm sağlayıcılar Ok ise 0, değilse 1."));
}

static bool HasFlag(string[] argv, string flag) =>
    argv.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

static string? GetOption(string[] argv, string name)
{
    for (var i = 0; i < argv.Length - 1; i++)
    {
        if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return argv[i + 1];
        }
    }

    return null;
}
