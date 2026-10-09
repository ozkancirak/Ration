using Ration.Core.Diagnostics;

namespace Ration.Core.Updates;

public sealed record UpdateCheckResult(
    bool IsInstalled,
    bool UpdateAvailable,
    string? AvailableVersion,
    bool Succeeded,
    DateTimeOffset CheckedAt);

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

    public UpdateService(IUpdateSource source, IUpdateStateStore store, Func<DateTimeOffset>? clock = null)
    {
        _source = source;
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public UpdateCheckResult? LastResult => _store.Load();

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

    private void Save(UpdateCheckResult result)
    {
        try { _store.Save(result); }
        catch (Exception ex) { Trace.Error("updates", $"last-check write failed type={ex.GetType().Name}"); }
    }
}
