using Ration.Core.Diagnostics;

namespace Ration.Core.Updates;

public sealed record UpdateCheckResult(
    bool IsInstalled,
    bool UpdateAvailable,
    string? AvailableVersion,
    bool Succeeded,
    DateTimeOffset CheckedAt);

public enum UpdateDownloadResult
{
    Downloaded,
    /// <summary>Denetim yeni sürüm bulamadı (ya da kaynak artık sunmuyor).</summary>
    NothingToDownload,
    /// <summary>Ağ, disk ya da doğrulama hatası; uygulama etkilenmez, yeniden denenebilir.</summary>
    Failed,
    /// <summary>Başka bir indirme sürüyor.</summary>
    Busy,
}

/// <summary>Son denetimin sonucunun saklandığı yer.</summary>
public interface IUpdateStateStore
{
    UpdateCheckResult? Load();
    void Save(UpdateCheckResult result);
}

/// <summary>
/// Güncelleme denetimi. Kaynak ve saklama enjekte edilir; paket yalnızca kullanıcı
/// eylemiyle indirilir.
/// </summary>
public sealed class UpdateService
{
    private readonly IUpdateSource _source;
    private readonly IUpdateStateStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private AvailableUpdate? _found;
    private bool _downloading;

    public UpdateService(IUpdateSource source, IUpdateStateStore store, Func<DateTimeOffset>? clock = null)
    {
        _source = source;
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public UpdateCheckResult? LastResult => _store.Load();

    /// <summary>Kurulumdan çalışıyor mu; değilse denetimin anlamı yoktur.</summary>
    public bool IsInstalled => _source.IsInstalled;

    /// <summary>Bulunan güncelleme indirildi ve uygulanmaya hazır.</summary>
    public bool IsDownloaded { get; private set; }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = _clock();
        UpdateCheckResult result;
        try
        {
            if (!_source.IsInstalled)
            {
                result = new(false, false, null, false, checkedAt);
            }
            else
            {
                var update = await _source.CheckAsync(cancellationToken).ConfigureAwait(false);
                Remember(update);
                result = update is null
                    ? new(true, false, null, true, checkedAt)
                    : new(true, true, update.Version, true, checkedAt);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.Error("updates", $"check failed type={ex.GetType().Name}");
            result = new(true, false, null, false, checkedAt);
        }

        Save(result);
        return result;
    }

    /// <summary>
    /// Bulunan güncellemeyi indirir. Önceki oturumdan kalma "güncelleme var" bilgisinde kaynak
    /// nesnesi bellekte yoktur; o zaman önce yeniden bulunur. Başarısızlık uygulamayı etkilemez.
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_downloading) return UpdateDownloadResult.Busy;
        _downloading = true;
        try
        {
            var update = _found ?? await _source.CheckAsync(cancellationToken).ConfigureAwait(false);
            Remember(update);
            if (update is null) return UpdateDownloadResult.NothingToDownload;

            // Kaynak yüzdeyi 0-100 dışında verebilir; arayüze hep geçerli bir değer gider.
            await _source.DownloadAsync(update, percent => progress?.Report(Math.Clamp(percent, 0, 100)), cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(100);
            IsDownloaded = true;
            return UpdateDownloadResult.Downloaded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.Error("updates", $"download failed type={ex.GetType().Name}");
            IsDownloaded = false;
            return UpdateDownloadResult.Failed;
        }
        finally
        {
            _downloading = false;
        }
    }

    /// <summary>
    /// İndirilmiş güncellemeyi çıkışta uygulanacak şekilde zamanlar. True dönerse çağıran
    /// uygulamadan çıkmalıdır; güncelleme çıkıştan sonra kurulur ve uygulama yeniden açılır.
    /// </summary>
    public bool ApplyOnExitAndRestart()
    {
        if (!IsDownloaded || _found is null) return false;

        try
        {
            _source.ApplyOnExitAndRestart(_found);
            return true;
        }
        catch (Exception ex)
        {
            Trace.Error("updates", $"apply failed type={ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Saklanan "güncelleme var" bilgisi güncellemeden sonra bayat kalır; sürüm şu anki sürümden
    /// gerçekten yeni mi. Ön sürüm eki (-beta) aynı numaralı kararlı sürümden eski sayılır.
    /// </summary>
    public static bool IsNewer(string? available, string current)
    {
        if (!TryParse(available, out var a, out var aPre) || !TryParse(current, out var c, out var cPre)) return false;
        var order = a.CompareTo(c);
        return order > 0 || (order == 0 && aPre is null && cPre is not null);
    }

    private static bool TryParse(string? text, out Version version, out string? preRelease)
    {
        version = new Version();
        preRelease = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().TrimStart('v').Split('-', 2);
        if (parts.Length == 2) preRelease = parts[1];
        return Version.TryParse(parts[0], out version!);
    }

    private void Remember(AvailableUpdate? update)
    {
        if (update?.Version != _found?.Version) IsDownloaded = false;
        _found = update;
    }

    private void Save(UpdateCheckResult result)
    {
        try { _store.Save(result); }
        catch (Exception ex) { Trace.Error("updates", $"last-check write failed type={ex.GetType().Name}"); }
    }
}
