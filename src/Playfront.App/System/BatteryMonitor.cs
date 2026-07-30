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

    /// <summary>Battery percentage (0-100), or null when Windows can't report it right now.</summary>
    public int? Percent { get; private set; }

    /// <summary>
    /// Does this machine have a battery at all? False only when Windows explicitly says so, which is
    /// how a desktop PC answers.
    ///
    /// Deliberately NOT the same thing as <see cref="Percent"/> being null. A laptop or handheld
    /// briefly failing to report its charge also gives a null percentage, and treating the two alike
    /// would make the battery vanish from a machine that has one. Starts true so nothing disappears
    /// before the first reading.
    /// </summary>
    public bool HasBattery { get; private set; } = true;

    /// <summary>Windows reports the battery is actively gaining charge. This goes false as soon as it
    /// reaches 100%, even while still plugged in — which is why <see cref="IsPluggedIn"/> is usually
    /// the one you want for "on mains power".</summary>
    public bool IsCharging { get; private set; }

    /// <summary>Mains power is connected, whether or not the battery is already full.</summary>
    public bool IsPluggedIn { get; private set; }

    public void Refresh()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            // The call itself failed. Says nothing about whether a battery exists, so HasBattery is
            // left alone.
            Percent = null;
            IsCharging = false;
            IsPluggedIn = false;
            return;
        }

        // The one flag that means "this machine has no battery". Only this one hides the indicator;
        // Unknown below is a temporary "can't tell", not the same claim.
        HasBattery = status.BatteryFlag != NoSystemBattery;

        if (!HasBattery || status.BatteryFlag == Unknown)
        {
            Percent = null;
            IsCharging = false;
            IsPluggedIn = status.ACLineStatus == AcOnline;
            return;
        }

        Percent = status.BatteryLifePercent == Unknown ? null : status.BatteryLifePercent;
        IsCharging = (status.BatteryFlag & ChargingFlag) != 0;
        IsPluggedIn = status.ACLineStatus == AcOnline;
    }
}
