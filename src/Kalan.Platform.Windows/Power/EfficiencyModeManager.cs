using System.Diagnostics;
using System.Runtime.InteropServices;
using Kalan.Platform.Windows.Interop;

namespace Kalan.Platform.Windows.Power;

public static class EfficiencyModeManager
{
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
}
