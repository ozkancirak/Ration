using System.Runtime.InteropServices;
using System.Security.Principal;
using Ration.Core.Diagnostics;

namespace Ration.Platform.Windows.App;

/// <summary>
/// Aynı Windows kullanıcısının Ration örneklerini tek mutex altında toplar.
/// İkinci örnek UI kurmaz; çalışan örneğe kayıtlı pencere mesajı yollar ve çıkar.
/// </summary>
public sealed class SingleInstanceLease : IDisposable
{
    private const string WakeMessageName = "Ration.SingleInstance.Wake.v1";
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private static readonly uint WakeMessageId = RegisterWindowMessage(WakeMessageName);

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static uint WakeWindowMessageId => WakeMessageId;

    public static string MutexNameForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            sid = Environment.UserName;
        }

        return $"Local\\Ration-{sid}";
    }

    /// <returns>true if this process owns the instance, or if diagnostics bypass the mutex.</returns>
    public static bool TryAcquire(string[] args, out SingleInstanceLease? lease)
    {
        lease = null;
        if (ShouldBypass(args))
        {
            return true;
        }

        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexNameForCurrentUser(), out bool createdNew);
            if (createdNew)
            {
                lease = new SingleInstanceLease(mutex);
                Trace.Info("single-instance", "acquired");
                return true;
            }

            mutex.Dispose();
            bool posted = WakeExistingInstance();
            Trace.Info("single-instance", $"duplicate forwarded={(posted ? "yes" : "no")}");
            return false;
        }
        catch (Exception ex)
        {
            // Local mutex creation should not fail for the interactive user. If it
            // does, do not risk starting a second instance.
            Trace.Error("single-instance", $"mutex error type={ex.GetType().Name}");
            return false;
        }
    }

    public static bool WakeExistingInstance()
    {
        if (WakeMessageId == 0)
        {
            return false;
        }

        return PostMessage(HwndBroadcast, WakeMessageId, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool ShouldBypass(string[] args) =>
        args.Any(argument => argument.Equals("--selftest", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--discover", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--raw", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--log", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--menu", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--settings", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--show", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException) { }
        finally
        {
            _mutex.Dispose();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);
}
