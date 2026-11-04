using Microsoft.UI.Dispatching;
using Ration.Core.Diagnostics;
using Ration.Core.Updates;

namespace Ration.App.Updates;

/// <summary>
/// Silently checks for updates once a day while the app is idle. So as not to delay startup, the first
/// look happens after a short wait; after that, every half hour it checks "is it time yet".
/// Checking and installing are separate choices: a check downloads nothing, and only when "install
/// automatically" is on does a found update get downloaded and scheduled for the next exit.
/// It does not go to the network when running without an install (portable).
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
            if (_service.IsInstalled && !_isBusy())
            {
                if (AppUpdates.AutoCheck && UpdateSchedule.IsDue(_service.LastResult, DateTimeOffset.UtcNow))
                {
                    await _service.CheckAsync();
                }

                // Installing is its own choice, so it also picks up an update that a manual check found.
                if (AppUpdates.AutoInstall && !_service.IsApplyScheduled && AppUpdates.HasNewerUpdate(_service.LastResult))
                {
                    await InstallQuietlyAsync();
                }
            }
        }
        finally
        {
            _timer.Interval = PollInterval;
            _timer.Start();
        }
    }

    private async Task InstallQuietlyAsync()
    {
        if (await _service.DownloadAsync() == UpdateDownloadResult.Downloaded && _service.ApplyOnExit())
        {
            Trace.Info("updates", "automatic install scheduled for the next exit");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
