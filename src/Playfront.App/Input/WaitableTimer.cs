using System;
using System.Runtime.InteropServices;

namespace Playfront.App.Input;

/// <summary>
/// A precise wait, for loops that need an even cadence.
///
/// Thread.Sleep cannot do this. Windows' default clock granularity is 15.6 ms, so asking it for 4 ms
/// gives 16.68 on average and anything from 13 to 42 - measured on this machine. A pointer loop
/// built on that runs at a quarter of the rate it thinks, with gaps varying threefold, and the
/// movement looks like it is stepping rather than gliding.
///
/// The alternative usually reached for is timeBeginPeriod(1), which raises the clock rate for the
/// WHOLE system. That is rude on a handheld running off a battery. A high-resolution waitable timer
/// gives the same precision for this thread only, and has been available since Windows 10 1803 -
/// comfortably below the Windows 10 1809 this project targets.
/// </summary>
public sealed class WaitableTimer : IDisposable
{
    private const uint CreateHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint InfiniteWait = 0xFFFFFFFF;

    private IntPtr _handle;

    private WaitableTimer(IntPtr handle) => _handle = handle;

    /// <summary>Creates one, or null when this Windows cannot provide a high-resolution timer.</summary>
    public static WaitableTimer? Create()
    {
        // AUTO-reset, not manual, and it is measurable: over 50 waits of 4 ms on this machine,
        // auto-reset came back between 4.17 and 5.62 ms while manual-reset ranged 4.14 to 10.37.
        // Same average, twice the spread - and it is the spread that shows up as jerky movement.
        // Without the high-resolution flag at all: 15.79 ms average, the same as Thread.Sleep.
        var h = CreateWaitableTimerEx(IntPtr.Zero, null, CreateHighResolution, TimerAllAccess);

        return h == IntPtr.Zero ? null : new WaitableTimer(h);
    }

    /// <summary>Blocks for the given time.</summary>
    public void WaitFor(double seconds)
    {
        if (_handle == IntPtr.Zero || seconds <= 0)
        {
            return;
        }

        // Negative means RELATIVE, in units of 100 nanoseconds. A positive value would be read as an
        // absolute date in 1601 and return immediately.
        var due = -(long)(seconds * 10_000_000);
        if (due >= 0)
        {
            return;
        }

        if (SetWaitableTimerEx(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
        {
            WaitForSingleObject(_handle, InfiniteWait);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period,
        IntPtr routine, IntPtr routineArg, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
