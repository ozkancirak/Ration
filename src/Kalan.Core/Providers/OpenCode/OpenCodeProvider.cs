using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kalan.Core.Abstractions;
using Kalan.Core.Model;

namespace Kalan.Core.Providers.OpenCode;

public sealed record OpenCodeCredentials(string AccessToken);

/// <summary>
/// OpenCode auth.json'ı salt okunur okur. OPENCODE_AUTH_CONTENT ayarlıysa
/// dosyanın önüne geçer; bu değer de hiçbir zaman loglanmaz.
/// </summary>
public static class OpenCodeCredentialStore
{
    public static OpenCodeCredentials? TryRead(string? path = null, string? contentOverride = null)
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

    private static OpenCodeCredentials? Parse(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return Parse(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static OpenCodeCredentials? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("opencode-go", out var entry) ||
            entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("key", out var keyElement) ||
            keyElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var key = keyElement.GetString();
        return string.IsNullOrWhiteSpace(key) ? null : new OpenCodeCredentials(key);
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
    private readonly string _databasePath;
    private readonly string _databaseCacheDirectory;

    public SourceKind Kind => SourceKind.LocalFile;

    public int? LastStatusCode { get; private set; }

    public OpenCodeUsageSource(
        HttpClient http,
        Func<OpenCodeCredentials?>? credentials = null,
        string? databasePath = null,
        string? databaseCacheDirectory = null)
    {
        _http = http;
        _credentials = credentials ?? (() => OpenCodeCredentialStore.TryRead());
        _databasePath = databasePath ?? KnownPaths.OpenCodeDatabaseFile;
        _databaseCacheDirectory = databaseCacheDirectory ?? KnownPaths.OpenCodeDatabaseCacheDir;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        LastStatusCode = null;
        var credentials = _credentials();

        if (credentials is not null)
        {
            return await FetchRemoteAsync(credentials, ct).ConfigureAwait(false);
        }

        if (!File.Exists(_databasePath))
        {
            return Snapshot.Empty("opencode", ProviderStatus.NotInstalled,
                "OpenCode kurulu değil veya opencode.db bulunamadı.", Kind);
        }

        try
        {
            var cost = OpenCodeLocalUsageReader.Read(
                _databasePath,
                _databaseCacheDirectory,
                ct);

            if (cost is null)
            {
                return Snapshot.Empty("opencode", ProviderStatus.Degraded,
                    "OpenCode yerel veritabanı okunamadı.", Kind);
            }

            return new UsageSnapshot(
                ProviderId: "opencode",
                Windows: Array.Empty<UsageWindow>(),
                Credits: null,
                Cost: cost,
                Status: ProviderStatus.Ok,
                ResolvedVia: Kind,
                FetchedAt: DateTimeOffset.UtcNow,
                StaleReason: "Yerel OpenCode token sayımı · son 30 gün",
                PlanName: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Snapshot.Empty("opencode", ProviderStatus.Degraded,
                $"OpenCode yerel veritabanı okunamadı: {ex.GetType().Name}", Kind);
        }
    }

    private async Task<UsageSnapshot> FetchRemoteAsync(
        OpenCodeCredentials credentials,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            LastStatusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Snapshot.Empty("opencode", ProviderStatus.AuthRequired,
                    "OpenCode anahtarı geçersiz.", Kind);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new UsageSnapshot(
                    ProviderId: "opencode",
                    Windows: Array.Empty<UsageWindow>(),
                    Credits: null,
                    Cost: null,
                    Status: ProviderStatus.Ok,
                    ResolvedVia: Kind,
                    FetchedAt: DateTimeOffset.UtcNow,
                    StaleReason: "OpenCode Go aboneliği yok — kota yok.",
                    PlanName: "OpenCode Go");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Snapshot.Empty("opencode", ProviderStatus.Error,
                    $"OpenCode kullanım yanıtı: HTTP {(int)response.StatusCode}", Kind);
            }

            var windows = OpenCodeUsageParser.ParseWindows(body);
            return new UsageSnapshot(
                ProviderId: "opencode",
                Windows: windows,
                Credits: null,
                Cost: null,
                Status: windows.Count > 0 ? ProviderStatus.Ok : ProviderStatus.Degraded,
                ResolvedVia: Kind,
                FetchedAt: DateTimeOffset.UtcNow,
                StaleReason: windows.Count > 0
                    ? null
                    : "OpenCode kullanım yanıtında rolling/weekly/monthly bulunamadı.",
                PlanName: "OpenCode Go");
        }
        catch (JsonException ex)
        {
            return Snapshot.Empty("opencode", ProviderStatus.Error,
                $"OpenCode JSON ayrıştırılamadı: {ex.Message}", Kind);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Snapshot.Empty("opencode", ProviderStatus.Error,
                $"OpenCode ağ hatası: {ex.GetType().Name}", Kind);
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
            "Rolling · $ limiti (5 saat)", windows);
        AddWindow(usage, "weekly", WindowKind.Weekly, TimeSpan.FromDays(7),
            "Haftalık · $ limiti (7 gün)", windows);
        AddWindow(usage, "monthly", WindowKind.Monthly, TimeSpan.FromDays(30),
            "Aylık · $ limiti (30 gün)", windows);
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
