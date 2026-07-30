using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Playfront.App.System;

/// <summary>Outcome of a settings change: what to say on screen when it did not work.</summary>
public readonly record struct TimeChangeResult(bool Ok, string? Error)
{
    public static TimeChangeResult Success => new(true, null);
    public static TimeChangeResult Failed(string why) => new(false, why);
}

/// <summary>
/// Reads and writes the Windows time settings behind Settings -> System -> Time: the time zone, the
/// automatic daylight-saving adjustment, and the 12/24-hour clock.
///
/// Everything here goes straight at the Windows API - no Settings app, no tzutil, no shelling out.
/// A time zone change lands in a few milliseconds and the running clock reflects it immediately.
///
/// Privileges, which decide what is possible without elevation (checked against the machine policy,
/// not assumed):
///   SeTimeZonePrivilege   -> LOCAL SERVICE, Administrators AND Users. The app can change the time
///                            zone itself, as the logged-in user, with no prompt. It ships DISABLED
///                            in the token, so it has to be switched on first - hence EnablePrivilege.
///   SeSystemtimePrivilege -> LOCAL SERVICE and Administrators only. Setting the clock to an
///                            arbitrary instant is NOT possible here and would have to go through the
///                            helper service. Nothing in this file tries.
///
/// The 12/24-hour clock is per-user data under HKCU, so it needs no privilege at all.
///
/// This works the same whether Playfront is the shell or is running as a window inside Windows: both
/// run as the logged-in user, with the same token.
/// </summary>
public static class SystemTimeSettings
{
    private const int ErrorSuccess = 0;
    private const int ErrorNoMoreItems = 259;

    /// <summary>The zone Windows is currently on, e.g. "Romance Standard Time".</summary>
    public static string CurrentTimeZoneId()
    {
        try
        {
            if (GetDynamicTimeZoneInformation(out var info) != TimeZoneIdInvalid)
            {
                return info.TimeZoneKeyName;
            }
        }
        catch
        {
            // Fall through to the managed value below.
        }

        return TimeZoneInfo.Local.Id;
    }

    /// <summary>Is Windows set to move the clock for daylight saving on its own?</summary>
    public static bool AutoDaylightSaving()
    {
        try
        {
            return GetDynamicTimeZoneInformation(out var info) == TimeZoneIdInvalid
                   || !info.DynamicDaylightTimeDisabled;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Switches Windows to another time zone. The clock moves as soon as this returns.</summary>
    public static TimeChangeResult SetTimeZone(string id) => ApplyZone(id, AutoDaylightSaving());

    /// <summary>Turns the automatic daylight-saving adjustment on or off for the current zone.</summary>
    public static TimeChangeResult SetAutoDaylightSaving(bool enabled) =>
        ApplyZone(CurrentTimeZoneId(), enabled);

    /// <summary>
    /// Writes one zone plus one daylight-saving choice. Both settings go through here because
    /// SetDynamicTimeZoneInformation writes the WHOLE structure - changing either one alone would
    /// quietly overwrite the other.
    /// </summary>
    private static TimeChangeResult ApplyZone(string id, bool autoDaylight)
    {
        // Always rebuilt from what the registry holds for this zone, never from the live structure.
        // Turning the adjustment off blanks the switchover dates below, so the live copy is a lossy
        // source: reading it back would leave the dates blank forever and the adjustment could never
        // be turned on again.
        if (!TryFindZone(id, out var target))
        {
            return TimeChangeResult.Failed($"Windows does not know the time zone '{id}'.");
        }

        target.DynamicDaylightTimeDisabled = !autoDaylight;

        if (!autoDaylight)
        {
            // The flag ALONE does not stop the clock jumping, and the failure is silent: the
            // registry and TimeZoneInfo both report standard time while the kernel keeps applying
            // the daylight bias, so the clock on screen simply does not move. A month of zero in
            // the switchover dates is what tells Windows this zone has no transition, and that is
            // what actually moves the clock.
            target.StandardDate = default;
            target.DaylightDate = default;
        }

        return Apply(ref target);
    }

    private static TimeChangeResult Apply(ref DynamicTimeZoneInformation info)
    {
        if (!EnablePrivilege("SeTimeZonePrivilege", out var why))
        {
            return TimeChangeResult.Failed(why);
        }

        if (!SetDynamicTimeZoneInformation(ref info))
        {
            var code = Marshal.GetLastWin32Error();
            return TimeChangeResult.Failed($"Windows refused the change (error {code}).");
        }

        // .NET caches the local zone for the life of the process, so without this DateTime.Now keeps
        // answering in the OLD zone even though Windows has already moved - the clock on screen would
        // not budge until the app was restarted.
        TimeZoneInfo.ClearCachedData();

        // Tell everything else on the desktop the clock moved. SendNotify, not Send: the broadcast
        // must not sit waiting on some other window that is not pumping messages.
        SendNotifyMessage(HwndBroadcast, WmTimeChange, IntPtr.Zero, IntPtr.Zero);
        SendNotifyMessage(HwndBroadcast, WmSettingChange, IntPtr.Zero, "intl");
        return TimeChangeResult.Success;
    }

    private static bool TryFindZone(string id, out DynamicTimeZoneInformation found)
    {
        // Enumerating asks Windows to fill the whole structure - bias, both switchover dates, both
        // names - from its own registry data. Building it by hand from the raw TZI blob is the other
        // way to do this and gets the dynamic (per-year) rules wrong.
        for (uint i = 0; ; i++)
        {
            var status = EnumDynamicTimeZoneInformation(i, out var candidate);
            if (status == ErrorNoMoreItems)
            {
                break;
            }

            if (status != ErrorSuccess)
            {
                continue;
            }

            if (string.Equals(candidate.TimeZoneKeyName, id, StringComparison.OrdinalIgnoreCase))
            {
                found = candidate;
                return true;
            }
        }

        found = default;
        return false;
    }

    // ---- 12/24-hour clock -------------------------------------------------------------------

    private const string IntlKey = @"Control Panel\International";

    /// <summary>
    /// Is the account showing time as 13:45 rather than 1:45 PM?
    ///
    /// Read from Windows EVERY TIME, deliberately. This was cached once, to save a registry read on
    /// each clock tick, and the cache went stale the moment anything changed the setting from outside
    /// the app - the Windows Settings app, another tool - leaving the checkbox on screen claiming the
    /// opposite of what Windows was doing. The read costs microseconds and the clocks tick once a
    /// second at worst; a wrong tick mark costs trust.
    /// </summary>
    public static bool Use24HourClock()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(IntlKey);
            // sShortTime is what the taskbar and most apps actually render. Capital H means 24-hour;
            // iTime is only consulted when the pattern is missing, because a user who edited the
            // pattern by hand can leave the two disagreeing.
            if (key?.GetValue("sShortTime") is string pattern && pattern.Length > 0)
            {
                return pattern.Contains('H');
            }

            return key?.GetValue("iTime") as string == "1";
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Format string for the app's own clocks, matching the account's choice.</summary>
    public static string ClockFormat => Use24HourClock() ? "H:mm" : "h:mm tt";

    /// <summary>
    /// Switches the account between the 12 and 24-hour clock. This is a real Windows setting, so the
    /// taskbar and other apps follow it too.
    /// </summary>
    public static TimeChangeResult Set24HourClock(bool use24Hour)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(IntlKey, writable: true);
            if (key is null)
            {
                return TimeChangeResult.Failed("The Windows regional settings could not be opened.");
            }

            // The leading-zero preference is the user's and survives the switch, which is why the
            // patterns are derived rather than written as four fixed strings.
            var padded = key.GetValue("iTLZero") as string == "1";
            var hour = use24Hour ? (padded ? "HH" : "H") : (padded ? "hh" : "h");
            var suffix = use24Hour ? string.Empty : " tt";

            key.SetValue("sShortTime", $"{hour}:mm{suffix}", RegistryValueKind.String);
            key.SetValue("sTimeFormat", $"{hour}:mm:ss{suffix}", RegistryValueKind.String);
            key.SetValue("iTime", use24Hour ? "1" : "0", RegistryValueKind.String);

            CultureInfo.CurrentCulture.ClearCachedData();

            // Tells the rest of the desktop the regional format changed. Measured: inside Playfront the
            // change is instant, and the Windows 11 taskbar clock follows within a minute - it only
            // re-reads the format when it redraws, which it does once a minute. Sending this blocking
            // (SendMessageTimeout) instead makes no difference: both were measured at 59.9 s when timed
            // from the start of a minute. Not worth blocking the UI thread for nothing.
            SendNotifyMessage(HwndBroadcast, WmSettingChange, IntPtr.Zero, "intl");
            return TimeChangeResult.Success;
        }
        catch (Exception e)
        {
            return TimeChangeResult.Failed($"The clock format could not be changed: {e.Message}");
        }
    }

    // ---- privileges -------------------------------------------------------------------------

    // Every privilege a process holds starts disabled; holding one and having it switched on are
    // different things. This turns it on for this process only, and only for the call that follows.
    private static bool EnablePrivilege(string name, out string error)
    {
        error = string.Empty;

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            error = "The Windows permissions of this process could not be read.";
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
            {
                error = $"Windows does not recognise the permission {name}.";
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);

            // AdjustTokenPrivileges reports success even when it changed nothing, so the error code
            // is the only way to tell that the account simply does not hold this permission.
            if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
            {
                error = "This Windows account is not allowed to change the time zone.";
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---- interop ----------------------------------------------------------------------------

    private const int TimeZoneIdInvalid = unchecked((int)0xFFFFFFFF);
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;
    private const int WmTimeChange = 0x001E;
    private const int WmSettingChange = 0x001A;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DynamicTimeZoneInformation
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        public SystemTime StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        public SystemTime DaylightDate;
        public int DaylightBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string TimeZoneKeyName;
        [MarshalAs(UnmanagedType.U1)] public bool DynamicDaylightTimeDisabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    // Declared for exactly one privilege. The real TOKEN_PRIVILEGES ends in an array, and shaping it
    // as a single entry is what lets it be passed by ref without manual marshalling.
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetDynamicTimeZoneInformation(out DynamicTimeZoneInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDynamicTimeZoneInformation(ref DynamicTimeZoneInformation info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumDynamicTimeZoneInformation(uint index, out DynamicTimeZoneInformation info);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessage(IntPtr window, int message, IntPtr wParam, string? lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
