using System.Net;
using System.Text.Json;
using Ration.Core.Abstractions;
using Ration.Core.Model;
using RationTrace = Ration.Core.Diagnostics.Trace;

namespace Ration.Core.Providers.Cursor;

public sealed record CursorUsageData(IReadOnlyList<UsageWindow> Windows, string? PlanName);

/// <summary>
/// cursor.com/api/usage-summary yanıtını ayrıştırır. Şema resmî olarak belgelenmedi; bu yüzden
/// ayrıştırma KASITLI olarak toleranslıdır ve tanımadığı alanı sessizce atlar: eksik veri
/// gösteririz, uydurma veri üretmeyiz. Beklenen biçim:
///
///   membershipType            "pro" | "free" | ...
///   billingCycleStart / End   ISO-8601
///   individualUsage.plan      { used, limit, totalPercentUsed? }   (dahil kullanım)
///   individualUsage.onDemand  { enabled, used, limit }             (isteğe bağlı harcama)
///
/// used/limit birimi (istek ya da sent) önemsizdir; yalnızca oran kullanılır.
/// </summary>
public static class CursorUsageParser
{
    public static CursorUsageData Parse(string json)
    {
        var windows = new List<UsageWindow>();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new CursorUsageData(windows, null);

        var resetsAt = ReadTimestamp(root, "billingCycleEnd");
        var start = ReadTimestamp(root, "billingCycleStart");
        TimeSpan? length = start is { } s && resetsAt is { } e && e > s ? e - s : null;

        if (root.TryGetProperty("individualUsage", out var individual) &&
            individual.ValueKind == JsonValueKind.Object)
        {
            if (individual.TryGetProperty("plan", out var plan) &&
                ReadPercent(plan, preferReportedPercent: true) is { } planPercent)
            {
                windows.Add(new UsageWindow(
                    WindowKind.Monthly, planPercent.Used, planPercent.Limit, planPercent.Percent,
                    resetsAt, L.T("Plan", "Plan"), length));
            }

            if (individual.TryGetProperty("onDemand", out var onDemand) &&
                onDemand.ValueKind == JsonValueKind.Object &&
                !(onDemand.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) &&
                ReadPercent(onDemand, preferReportedPercent: false) is { Limit: > 0 } demand)
            {
                windows.Add(new UsageWindow(
                    WindowKind.Monthly, demand.Used, demand.Limit, demand.Percent,
                    resetsAt, L.T("On-demand", "İsteğe bağlı"), length));
            }
        }

        return new CursorUsageData(windows, FormatPlan(ReadString(root, "membershipType")));
    }

    /// <summary>"pro" → "Pro", "free_trial" → "Free trial"; boşsa null.</summary>
    public static string? FormatPlan(string? membership)
    {
        if (string.IsNullOrWhiteSpace(membership)) return null;

        var text = membership.Trim().Replace('_', ' ').Replace('-', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private readonly record struct Usage(double Used, double? Limit, double Percent);

    private static Usage? ReadPercent(JsonElement element, bool preferReportedPercent)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var used = JsonHelpers.ReadFirstNumber(element, "used");
        var limit = JsonHelpers.ReadFirstNumber(element, "limit");

        double percent;
        if (preferReportedPercent && JsonHelpers.ReadFirstNumber(element, "totalPercentUsed") is { } reported)
        {
            percent = reported;
        }
        else if (used is { } u && limit is > 0)
        {
            percent = u / limit.Value * 100;
        }
        else
        {
            return null;
        }

        // Dahil kullanım hakkı olmayan plan (ör. ücretsiz): limit 0 ve kullanım 0 "%0 kullanıldı"
        // değil "sınır yok" demektir; pencere gösterilmez.
        if (limit is not > 0 && percent <= 0) return null;

        return new Usage(used ?? 0, limit is > 0 ? limit : null, Math.Clamp(percent, 0, 100));
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) ? JsonHelpers.ReadTimestamp(element) : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Cursor uygulamasının yerel oturum belirtecini, Cursor'ın kendi adresine (cursor.com) çerez
/// olarak gönderip plan kullanımını okur. Belirteç yerel dosyadan gelir (Kind = LocalFile);
/// Ration onu yenilemez, kaydetmez, günlüğe yazmaz.
/// </summary>
public sealed class CursorUsageSource : IUsageSource
{
    public const string UsageEndpoint = "https://cursor.com/api/usage-summary";

    private readonly HttpClient _http;
    private readonly Func<bool> _installed;
    private readonly Func<CursorSession?> _session;
    private readonly Func<bool> _encryptedToken;
    private readonly Func<DateTimeOffset> _clock;

    public CursorUsageSource(
        HttpClient http,
        Func<CursorSession?>? session = null,
        Func<bool>? installed = null,
        Func<DateTimeOffset>? clock = null,
        Func<bool>? encryptedToken = null,
        Func<string, string?>? decryptToken = null)
    {
        _http = http;
        _session = session ?? (() => CursorSessionStore.TryRead(decrypt: decryptToken));
        _installed = installed ?? (() => File.Exists(KnownPaths.CursorStateDatabaseFile));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _encryptedToken = encryptedToken ?? (() => CursorSessionStore.HasEncryptedToken());
    }

    public SourceKind Kind => SourceKind.LocalFile;

    // Kurulu değilse "kullanılabilir kaynak yok" yerine açık bir NotInstalled durumu dönmesi için
    // kaynak her zaman denenir.
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        if (!_installed())
        {
            return Snapshot.Empty("cursor", ProviderStatus.NotInstalled,
                L.T("Cursor is not installed or has never been opened.", "Cursor kurulu değil ya da hiç açılmamış."), Kind);
        }

        var session = _session();
        if (session is null)
        {
            // Giriş yapılmış ama belirteç şifreli (Cursor 3.27+): "giriş yapın" demek yanıltır.
            return Snapshot.Empty("cursor", ProviderStatus.AuthRequired,
                _encryptedToken()
                    ? L.T("Cursor stores its sign-in encrypted and Ration cannot read it yet.", "Cursor girişi şifreli saklıyor ve Ration henüz okuyamıyor.")
                    : L.T("Sign in to Cursor, then try again.", "Cursor'da giriş yapın, sonra tekrar deneyin."), Kind);
        }

        if (!session.IsUsable(_clock()))
        {
            return Snapshot.Empty("cursor", ProviderStatus.AuthRequired,
                L.T("Cursor session expired — open Cursor once to renew it.", "Cursor oturumu doldu — yenilemek için Cursor'ı bir kez açın."), Kind);
        }

        HttpRequestMessage NewRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={session.UserId}%3A%3A{session.AccessToken}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return request;
        }

        RationTrace.Info(
            "provider.http",
            "provider=cursor request-method=GET request-url=" + UsageEndpoint + " request-header-names=Cookie,Accept");

        try
        {
            using var response = await TransientRetry.SendAsync(_http, NewRequest, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            RationTrace.Info("provider.http", $"provider=cursor endpoint=usage status={(int)response.StatusCode}");

            if ((int)response.StatusCode == 429)
            {
                return Snapshot.Empty("cursor", ProviderStatus.Error,
                    L.T("Rate limited (HTTP 429): too many requests, will retry shortly.", "Hız sınırı (HTTP 429): Çok fazla istek yapıldı, biraz sonra tekrar denenecek."), Kind);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Snapshot.Empty("cursor", ProviderStatus.AuthRequired,
                    L.T($"Cursor rejected the session (HTTP {(int)response.StatusCode}). Open Cursor and sign in again.", $"Cursor oturumu reddetti (HTTP {(int)response.StatusCode}). Cursor'ı açıp tekrar giriş yapın."), Kind);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Snapshot.Empty("cursor", ProviderStatus.Error,
                    L.T($"Unexpected response: HTTP {(int)response.StatusCode}", $"Beklenmedik yanıt: HTTP {(int)response.StatusCode}"), Kind);
            }

            var usage = CursorUsageParser.Parse(body);
            var plan = usage.PlanName ?? CursorUsageParser.FormatPlan(session.MembershipType);

            return new UsageSnapshot(
                ProviderId: "cursor",
                Windows: usage.Windows,
                Credits: null,
                Cost: null,
                Status: usage.Windows.Count > 0 ? ProviderStatus.Ok : ProviderStatus.Degraded,
                ResolvedVia: Kind,
                FetchedAt: _clock(),
                StaleReason: usage.Windows.Count > 0
                    ? null
                    : L.T("No usage limit is reported for this plan.", "Bu plan için kullanım sınırı bildirilmiyor."),
                PlanName: plan);
        }
        catch (JsonException ex)
        {
            RationTrace.Error("provider.http", $"provider=cursor endpoint=usage error={ex.GetType().Name}");
            return Snapshot.Empty("cursor", ProviderStatus.Error,
                L.T("The Cursor response could not be read.", "Cursor yanıtı okunamadı."), Kind);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            RationTrace.Error("provider.http", $"provider=cursor endpoint=usage error={ex.GetType().Name}");
            return Snapshot.Empty("cursor", ProviderStatus.Error, NetworkErrors.Describe(ex), Kind);
        }
    }
}

public sealed class CursorProvider : IUsageProvider
{
    public string Id => "cursor";

    public string DisplayName => "Cursor";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsSessionWindow: false,
        SupportsWeeklyWindow: false,
        SupportsMonthlyWindow: true,
        SupportsCredits: false,
        SupportsCostReport: false);

    public IReadOnlyList<IUsageSource> Sources { get; }

    /// <param name="http">
    /// Verilmezse çerez kabı kapalı özel bir istemci kurulur: belirteç elle Cookie başlığına konur,
    /// yanıttaki Set-Cookie başka isteklere taşınmaz.
    /// </param>
    /// <param name="decryptToken">Cursor 3.27+ şifreli belirtecini çözen platform işlevi (isteğe bağlı).</param>
    public CursorProvider(HttpClient? http = null, Func<string, string?>? decryptToken = null)
    {
        Sources = [new CursorUsageSource(http ?? CreateClient(), decryptToken: decryptToken)];
    }

    private static HttpClient CreateClient()
    {
        // Yönlendirme izlenmez: belirteç elle Cookie başlığında taşınıyor, 3xx ile başka bir
        // adrese gitmemeli. Yönlendirme "beklenmedik yanıt" olarak görünür.
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ration");
        return client;
    }
}
