using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Kalan.Platform.Windows.Interop;

namespace Kalan.Platform.Windows.Power;

public static class EfficiencyModeManager
{
    private static readonly object Gate = new();
    private static bool _efficiencyMode;
    private static bool _sessionLocked;
    private static bool _powerSaving;
    private static bool? _lastPauseState;

    static EfficiencyModeManager()
    {
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

            SystemEvents.PowerModeChanged += (_, _) => RefreshPowerSavingState();
        }
        catch
        {
            // SystemEvents may be unavailable in a restricted desktop context.
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

    public static bool ShouldPause
    {
        get
        {
            lock (Gate) return _efficiencyMode || _sessionLocked || _powerSaving;
        }
    }

    public static void SetEfficiencyMode(bool enable)
    {
        lock (Gate) _efficiencyMode = enable;
        NotifyPauseChanged();

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
        lock (Gate) shouldPause = _efficiencyMode || _sessionLocked || _powerSaving;

        lock (Gate)
        {
            if (_lastPauseState == shouldPause) return;
            _lastPauseState = shouldPause;
        }

        PauseChanged?.Invoke(shouldPause);
    }
}
