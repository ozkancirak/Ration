using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Ration.Platform.Windows.Interop;

namespace Ration.Platform.Windows.Power;

public static class EfficiencyModeManager
{
    private static readonly object Gate = new();
    private static bool _sessionLocked;
    private static bool _powerSaving;
    private static bool? _lastPauseState;

    static EfficiencyModeManager()
    {
        // SystemEvents delivers events with Send to the SynchronizationContext of the thread where the listener was
        // set up. On the WinUI UI thread this context swallowed the events: lock and power events never arrived.
        // When set up without a context, events run directly on the SystemEvents thread; the listeners are thread-safe.
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            SystemEvents.SessionSwitch += (_, e) =>
            {
                lock (Gate)
                {
                    _sessionLocked = e.Reason == SessionSwitchReason.SessionLock
                        ? true
                        : e.Reason == SessionSwitchReason.SessionUnlock
                            ? false
                            : _sessionLocked;
                }

                NotifyPauseChanged();
            };

            SystemEvents.PowerModeChanged += (_, e) =>
            {
                RefreshPowerSavingState();
                if (e.Mode == PowerModes.Resume) Resumed?.Invoke();
            };
        }
        catch
        {
            // SystemEvents may be unavailable in a restricted desktop context.
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        try
        {
            global::Windows.System.Power.PowerManager.EnergySaverStatusChanged += (_, _) => RefreshPowerSavingState();
        }
        catch
        {
            // EnergySaverStatus is not available on every supported Windows build.
        }

        RefreshPowerSavingState();
    }

    public static event Action<bool>? PauseChanged;

    /// <summary>The computer resumed from sleep; the network may not be ready yet.</summary>
    public static event Action? Resumed;

    // Only screen lock and Windows energy saver stop network refresh.
    // The efficiency mode (EcoQoS) of the hidden panel lowers CPU priority and does NOT stop refreshing:
    // it used to stop it, so the background refresh never ran while the panel was closed and
    // the tray icon stayed stale until the panel was opened.
    public static bool ShouldPause
    {
        get { lock (Gate) return _sessionLocked || _powerSaving; }
    }

    public static void SetEfficiencyMode(bool enable)
    {

        try
        {
            var throttle = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
            {
                Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enable ? NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };

            var processHandle = NativeMethods.GetCurrentProcess();
            NativeMethods.SetProcessInformation(
                processHandle,
                NativeMethods.ProcessPowerThrottling,
                ref throttle,
                (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESS_POWER_THROTTLING_STATE)));

            // Also adjust base process priority class
            using var current = Process.GetCurrentProcess();
            current.PriorityClass = enable ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
        }
        catch
        {
            // Ignored on systems that do not support EcoQoS / Efficiency Mode
        }
    }

    private static void RefreshPowerSavingState()
    {
        bool powerSaving;
        try
        {
            powerSaving = global::Windows.System.Power.PowerManager.EnergySaverStatus ==
                global::Windows.System.Power.EnergySaverStatus.On;
        }
        catch
        {
            powerSaving = false;
        }

        lock (Gate) _powerSaving = powerSaving;
        NotifyPauseChanged();
    }

    private static void NotifyPauseChanged()
    {
        bool shouldPause;
        lock (Gate) shouldPause = _sessionLocked || _powerSaving;

        lock (Gate)
        {
            if (_lastPauseState == shouldPause) return;
            _lastPauseState = shouldPause;
        }

        PauseChanged?.Invoke(shouldPause);
    }
}
