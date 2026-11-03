using Microsoft.UI.Dispatching;
using Ration.Core.Updates;

namespace Ration.App.Updates;

/// <summary>
/// Silently checks for updates once a day while the app is idle. So as not to delay startup, the first
/// look happens after a short wait; after that, every half hour it checks "is it time yet".
/// It only checks, downloads nothing. It does not go to the network when running without an install (portable).
/// </summary>
internal sealed class UpdateAutoCheck : IDisposable
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    private readonly DispatcherQueueTimer _timer;
    private readonly UpdateService _service;
    private readonly Func<bool> _isBusy;

    /// <param name="isBusy">True if the user is busy with a window; the check is postponed.</param>
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
