using Microsoft.UI.Dispatching;
using Ration.Core.Updates;

namespace Ration.App.Updates;

/// <summary>
/// Günde bir, uygulama boştayken sessizce güncelleme denetler. Açılışı geciktirmemek için ilk
/// bakış kısa bir beklemeden sonra yapılır; sonra yarım saatte bir "vakti geldi mi" diye bakılır.
/// Yalnızca denetler, hiçbir şey indirmez. Kurulumsuz (taşınabilir) çalışmada ağa çıkmaz.
/// </summary>
internal sealed class UpdateAutoCheck : IDisposable
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    private readonly DispatcherQueueTimer _timer;
    private readonly UpdateService _service;
    private readonly Func<bool> _isBusy;

    /// <param name="isBusy">Kullanıcı bir pencereyle uğraşıyorsa true; denetim sonraya kalır.</param>
    public UpdateAutoCheck(DispatcherQueue queue, UpdateService service, Func<bool> isBusy)
    {
        _service = service;
        _isBusy = isBusy;
        _timer = queue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Interval = FirstDelay;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private async void OnTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (_service.IsInstalled && !_isBusy() && UpdateSchedule.IsDue(_service.LastResult, DateTimeOffset.UtcNow))
            {
                await _service.CheckAsync();
            }
        }
        finally
        {
            _timer.Interval = PollInterval;
            _timer.Start();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
