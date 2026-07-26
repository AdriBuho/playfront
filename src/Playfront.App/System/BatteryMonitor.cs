using System.Runtime.InteropServices;

namespace Playfront.App.System;

/// <summary>
/// Lee el estado real de la batería de Windows vía la API nativa <c>GetSystemPowerStatus</c>
/// (kernel32.dll) — igual de directo que leer el mando por XInput, sin librerías externas.
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

    /// <summary>Porcentaje de batería (0-100), o null si Windows no sabe darlo (p.ej. sin batería).</summary>
    public int? Percent { get; private set; }

    /// <summary>Windows dice que la batería está ganando carga activamente. Deja de ser true
    /// en cuanto llega al 100%, aunque siga enchufada — por eso normalmente interesa mas
    /// <see cref="IsPluggedIn"/> para saber si esta "conectada a la corriente".</summary>
    public bool IsCharging { get; private set; }

    /// <summary>El cable de corriente está conectado, este o no la batería ya al 100%.</summary>
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
