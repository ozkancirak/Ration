using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ration.Core.Abstractions;
using Ration.Core.Model;
using RationTrace = Ration.Core.Diagnostics.Trace;

namespace Ration.Core.Providers.OpenCode;

public sealed record OpenCodeCredentials(
    string AccessToken,
    IReadOnlyList<string>? ConfiguredProviders = null);

public sealed record OpenCodeAuthInfo(
    string? AccessToken,
    IReadOnlyList<string> ConfiguredProviders)
{
    public OpenCodeCredentials? ToCredentials() =>
        string.IsNullOrWhiteSpace(AccessToken)
            ? null
            : new OpenCodeCredentials(AccessToken, ConfiguredProviders);
}

/// <summary>
/// OpenCode auth.json'ı salt okunur okur. OPENCODE_AUTH_CONTENT ayarlıysa
/// dosyanın önüne geçer; bu değer de hiçbir zaman loglanmaz.
/// </summary>
public static class OpenCodeCredentialStore
{
    public static OpenCodeCredentials? TryRead(string? path = null, string? contentOverride = null)
    {
        return TryReadInfo(path, contentOverride)?.ToCredentials();
    }

    public static OpenCodeAuthInfo? TryReadInfo(string? path = null, string? contentOverride = null)
    {
        var environmentContent = Environment.GetEnvironmentVariable("OPENCODE_AUTH_CONTENT");
        if (environmentContent is not null)
        {
            return Parse(environmentContent);
        }

        if (contentOverride is not null)
        {
            return Parse(contentOverride);
        }

        path ??= KnownPaths.OpenCodeAuthFile;
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            return Parse(document.RootElement);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static OpenCodeAuthInfo? Parse(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return Parse(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static OpenCodeAuthInfo? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? key = null;
        if (root.TryGetProperty("opencode-go", out var entry) &&
            entry.ValueKind == JsonValueKind.Object &&
            entry.TryGetProperty("key", out var keyElement) &&
            keyElement.ValueKind == JsonValueKind.String)
        {
            key = keyElement.GetString();
        }

        var configuredProviders = root.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !name.Equals("opencode-go", StringComparison.OrdinalIgnoreCase))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new OpenCodeAuthInfo(
            string.IsNullOrWhiteSpace(key) ? null : key,
            configuredProviders);
    }
}

public sealed class OpenCodeProvider : IUsageProvider
{
    public string Id => "opencode";

    public string DisplayName => "OpenCode";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsSessionWindow: true,
        SupportsWeeklyWindow: true,
        SupportsMonthlyWindow: true,
        SupportsCredits: false,
        SupportsCostReport: true);

    public IReadOnlyList<IUsageSource> Sources { get; }

    public OpenCodeProvider(HttpClient http)
    {
        Sources = new IUsageSource[]
        {
            new OpenCodeUsageSource(http),
        };
    }
}

public sealed class OpenCodeUsageSource : IUsageSource
{
    public const string UsageEndpoint = "https://opencode.ai/zen/go/v1/usage";

    private readonly HttpClient _http;
    private readonly Func<OpenCodeCredentials?> _credentials;
    private readonly Func<OpenCodeAuthInfo?> _authInfo;
    private readonly string _databasePath;
    private readonly string _databaseCacheDirectory;
    private readonly string? _freeModelPath;

    private static string NoQuotaDetail =>
        L.T("OpenCode has no quota of its own; it uses your configured providers' subscriptions.", "OpenCode'un kendi kotası yok; yapılandırılmış sağlayıcıların aboneliğini kullanıyor.");

    public SourceKind Kind => SourceKind.LocalFile;

    public int? LastStatusCode { get; private set; }

    public OpenCodeUsageSource(
        HttpClient http,
        Func<OpenCodeCredentials?>? credentials = null,
        string? databasePath = null,
        string? databaseCacheDirectory = null,
        Func<OpenCodeAuthInfo?>? authInfo = null,
        string? freeModelPath = null)
    {
        _http = http;
        _authInfo = authInfo ?? (() => OpenCodeCredentialStore.TryReadInfo());
        _credentials = credentials ?? (() => _authInfo()?.ToCredentials());
        _databasePath = databasePath ?? KnownPaths.OpenCodeDatabaseFile;
        _databaseCacheDirectory = databaseCacheDirectory ?? KnownPaths.OpenCodeDatabaseCacheDir;
        _freeModelPath = freeModelPath ?? KnownPaths.OpenCodeFreeModelsFile;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        LastStatusCode = null;
        var authInfo = _authInfo();
        var credentials = _credentials() ?? authInfo?.ToCredentials();

        if (credentials is not null)
        {
            return await FetchRemoteAsync(credentials, ct).ConfigureAwait(false);
        }

        var configuredProviders = authInfo?.ConfiguredProviders ?? Array.Empty<string>();

        if (!File.Exists(_databasePath))
        {
            return LocalStatusSnapshot(
                ProviderStatus.NotInstalled,
                L.T("OpenCode is not installed or opencode.db was not found.", "OpenCode kurulu değil veya opencode.db bulunamadı."),
                configuredProviders);
        }

        try
        {
            var cost = OpenCodeLocalUsageReader.Read(
                _databasePath,
                _databaseCacheDirectory,
                ct,
                _freeModelPath);

            if (cost is null)
            {
                return LocalStatusSnapshot(
                    ProviderStatus.Degraded,
                    L.T("Could not read the local OpenCode database.", "OpenCode yerel veritabanı okunamadı."),
                    configuredProviders);
            }

            return new UsageSnapshot(
                ProviderId: "opencode",
                Windows: Array.Empty<UsageWindow>(),
                Credits: null,
                Cost: cost,
                Status: ProviderStatus.Ok,
                ResolvedVia: Kind,
                FetchedAt: DateTimeOffset.UtcNow,
                StaleReason: L.T("Local OpenCode token count · last 30 days", "Yerel OpenCode token sayımı · son 30 gün"),
                PlanName: L.T("No quota", "Kota yok"),
                StatusDetail: NoQuotaDetail,
                ConfiguredProviders: configuredProviders);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocalStatusSnapshot(
                ProviderStatus.Degraded,
                L.T($"Could not read the local OpenCode database: {ex.GetType().Name}", $"OpenCode yerel veritabanı okunamadı: {ex.GetType().Name}"),
                configuredProviders);
        }
    }

    private static UsageSnapshot LocalStatusSnapshot(
        ProviderStatus status,
        string reason,
        IReadOnlyList<string> configuredProviders) =>
        new(
            ProviderId: "opencode",
            Windows: Array.Empty<UsageWindow>(),
            Credits: null,
            Cost: null,
            Status: status,
            ResolvedVia: SourceKind.LocalFile,
            FetchedAt: DateTimeOffset.UtcNow,
            StaleReason: reason,
            PlanName: L.T("No quota", "Kota yok"),
            StatusDetail: NoQuotaDetail,
            ConfiguredProviders: configuredProviders);

    private async Task<UsageSnapshot> FetchRemoteAsync(
        OpenCodeCredentials credentials,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        var localCost = ReadLocalCost(ct);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            LastStatusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            RationTrace.Info(
                "provider.http",
                $"provider=opencode endpoint=usage status={(int)response.StatusCode}");

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new UsageSnapshot(
                    ProviderId: "opencode",
                    Windows: Array.Empty<UsageWindow>(),
                    Credits: null,
                    Cost: localCost,
                    Status: ProviderStatus.AuthRequired,
                    ResolvedVia: Kind,
                    FetchedAt: DateTimeOffset.UtcNow,
                    StaleReason: L.T("OpenCode key is invalid.", "OpenCode anahtarı geçersiz."));
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new UsageSnapshot(
                    ProviderId: "opencode",
                    Windows: Array.Empty<UsageWindow>(),
                    Credits: null,
                    Cost: localCost,
                    Status: ProviderStatus.Ok,
                    ResolvedVia: Kind,
                    FetchedAt: DateTimeOffset.UtcNow,
                    StaleReason: L.T("No OpenCode Go subscription — no quota.", "OpenCode Go aboneliği yok — kota yok."),
                    PlanName: L.T("No quota", "Kota yok"),
                    StatusDetail: L.T("No OpenCode Go subscription — server quota unavailable.", "OpenCode Go aboneliği yok — sunucu kotası kullanılamıyor."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UsageSnapshot(
                    ProviderId: "opencode",
                    Windows: Array.Empty<UsageWindow>(),
                    Credits: null,
                    Cost: localCost,
                    Status: ProviderStatus.Error,
                    ResolvedVia: Kind,
                    FetchedAt: DateTimeOffset.UtcNow,
                    StaleReason: L.T($"OpenCode usage response: HTTP {(int)response.StatusCode}", $"OpenCode kullanım yanıtı: HTTP {(int)response.StatusCode}"));
            }

            var windows = OpenCodeUsageParser.ParseWindows(body);
            return new UsageSnapshot(
                ProviderId: "opencode",
                Windows: windows,
                Credits: null,
                Cost: localCost,
                Status: windows.Count > 0 ? ProviderStatus.Ok : ProviderStatus.Degraded,
                ResolvedVia: Kind,
                FetchedAt: DateTimeOffset.UtcNow,
                StaleReason: windows.Count > 0
                    ? null
                    : L.T("No rolling/weekly/monthly in the OpenCode usage response.", "OpenCode kullanım yanıtında rolling/weekly/monthly bulunamadı."),
                PlanName: "OpenCode Go");
        }
        catch (JsonException ex)
        {
            RationTrace.Error("provider.http", $"provider=opencode endpoint=usage error={ex.GetType().Name}");
            return Snapshot.Empty("opencode", ProviderStatus.Error,
                L.T($"OpenCode JSON could not be parsed: {ex.Message}", $"OpenCode JSON ayrıştırılamadı: {ex.Message}"), Kind);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            RationTrace.Error("provider.http", $"provider=opencode endpoint=usage error={ex.GetType().Name}");
            return Snapshot.Empty("opencode", ProviderStatus.Error,
                L.T($"OpenCode network error: {ex.GetType().Name}", $"OpenCode ağ hatası: {ex.GetType().Name}"), Kind);
        }
    }

    private CostReport? ReadLocalCost(CancellationToken ct)
    {
        if (!File.Exists(_databasePath)) return null;

        try
        {
            return OpenCodeLocalUsageReader.Read(
                _databasePath,
                _databaseCacheDirectory,
                ct,
                _freeModelPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public static class OpenCodeUsageParser
{
    public static IReadOnlyList<UsageWindow> ParseWindows(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var usage = root.TryGetProperty("usage", out var nested) &&
                    nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;

        var windows = new List<UsageWindow>(3);
        AddWindow(usage, "rolling", WindowKind.Session, TimeSpan.FromHours(5),
            L.T("Rolling · $ limit (5 hours)", "Rolling · $ limiti (5 saat)"), windows);
        AddWindow(usage, "weekly", WindowKind.Weekly, TimeSpan.FromDays(7),
            L.T("Weekly · $ limit (7 days)", "Haftalık · $ limiti (7 gün)"), windows);
        AddWindow(usage, "monthly", WindowKind.Monthly, TimeSpan.FromDays(30),
            L.T("Monthly · $ limit (30 days)", "Aylık · $ limiti (30 gün)"), windows);
        return windows;
    }

    private static void AddWindow(
        JsonElement usage,
        string name,
        WindowKind kind,
        TimeSpan length,
        string label,
        List<UsageWindow> windows)
    {
        if (!usage.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var percent = ReadNumber(value, "percent");
        if (percent is null || !double.IsFinite(percent.Value)) return;

        DateTimeOffset? resetsAt = null;
        if (value.TryGetProperty("resetsAt", out var reset))
        {
            resetsAt = JsonHelpers.ReadTimestamp(reset);
        }

        var clamped = Math.Clamp(percent.Value, 0, 100);
        windows.Add(new UsageWindow(
            Kind: kind,
            Used: clamped,
            Limit: 100,
            Percent: clamped,
            ResetsAt: resetsAt,
            Label: label,
            WindowLength: length));
    }

    private static double? ReadNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }
}
