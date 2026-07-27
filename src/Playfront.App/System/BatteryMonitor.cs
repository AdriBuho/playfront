using System.Runtime.InteropServices;

namespace Playfront.App.System;

/// <summary>
/// Reads real battery state from Windows through the native <c>GetSystemPowerStatus</c>
/// (kernel32.dll) — as direct as reading the gamepad through XInput, with no external libraries.
/// </summary>
public sealed class BatteryMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private const byte NoSystemBattery = 128;
    private const byte Unknown = 255;
    private const byte ChargingFlag = 8;
    private const byte AcOnline = 1;

    /// <summary>Battery percentage (0-100), or null when Windows can't report it (no battery).</summary>
    public int? Percent { get; private set; }

    /// <summary>Windows reports the battery is actively gaining charge. This goes false as soon as it
    /// reaches 100%, even while still plugged in — which is why <see cref="IsPluggedIn"/> is usually
    /// the one you want for "on mains power".</summary>
    public bool IsCharging { get; private set; }

    /// <summary>Mains power is connected, whether or not the battery is already full.</summary>
    public bool IsPluggedIn { get; private set; }

    public void Refresh()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryFlag == Unknown || status.BatteryFlag == NoSystemBattery)
        {
            Percent = null;
            IsCharging = false;
            IsPluggedIn = false;
            return;
        }

        Percent = status.BatteryLifePercent == Unknown ? null : status.BatteryLifePercent;
        IsCharging = (status.BatteryFlag & ChargingFlag) != 0;
        IsPluggedIn = status.ACLineStatus == AcOnline;
    }
}
