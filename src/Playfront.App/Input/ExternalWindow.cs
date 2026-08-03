using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Playfront.App.Input;

/// <summary>
/// Finds and reshapes the window of a program Playfront launched, so it behaves like a console app
/// instead of a desktop one: no title bar, no close or minimise buttons, covering the whole screen.
///
/// The point is not decoration. With the title bar there, the user can minimise or close the program
/// with a pointer click and end up looking at a shell that still thinks it is driving a mouse. Taking
/// the buttons away leaves exactly one way out - the one Playfront knows about - so the two never
/// disagree about what is on screen.
///
/// Everything done here is REVERSIBLE and the original values are kept. Window styles live only in
/// the running window, so even in the worst case closing and reopening the program restores it.
/// </summary>
public static class ExternalWindow
{
    private const int GwlStyle = -16;

    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsSysMenu = 0x00080000;
    private const int WsBorder = 0x00800000;
    private const int WsDlgFrame = 0x00400000;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    // Stops Windows sending WM_WINDOWPOSCHANGING, the message a program answers to argue with the size
    // it was given - and they do argue: Spotify clamps itself to the screen plus 20 px, measured by
    // asking for 1144, 1200 and 1400 and getting 1100 back every time. Nothing needs a window that big
    // today, but the flag costs nothing and makes "cover the screen" mean it.
    private const uint SwpNoSendChanging = 0x0400;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private const int SwRestore = 9;

    private static IntPtr _window;
    private static int _originalStyle;
    private static Rect _originalRect;

    // Where the window was put, remembered rather than worked out again. Recomputing it would ask
    // "which monitor is it nearest to NOW", so a window dragged halfway to a second screen would be
    // snapped to that one instead of being brought back.
    private static Rect _target;

    /// <summary>Whether a window is currently being held fullscreen by us.</summary>
    public static bool Active => _window != IntPtr.Zero;

    /// <summary>
    /// The main window of a process by name, or zero. "Main" here is the first visible top-level
    /// window with a title: a program like Spotify owns several windows, and most are invisible
    /// helpers with no caption.
    /// </summary>
    public static IntPtr FindMainWindow(string processName)
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            ids.Add(p.Id);
            p.Dispose();
        }

        if (ids.Count == 0)
        {
            return IntPtr.Zero;
        }

        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h))
            {
                return true;
            }

            GetWindowThreadProcessId(h, out var pid);
            if (!ids.Contains((int)pid))
            {
                return true;
            }

            var len = GetWindowTextLength(h);
            if (len == 0)
            {
                return true;
            }

            found = h;
            return false; // stop
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Strips the frame off a window and stretches it over the whole monitor. Remembers what it was
    /// so <see cref="Restore"/> can undo it.
    /// </summary>
    /// <param name="window">The window to take over.</param>
    public static bool MakeFullscreen(IntPtr window)
    {
        if (window == IntPtr.Zero || Active)
        {
            return false;
        }

        try
        {
            // A minimised window cannot be measured or resized sensibly; bring it up first.
            ShowWindow(window, SwRestore);

            if (!GetWindowRect(window, out _originalRect))
            {
                return false;
            }

            _originalStyle = GetWindowLong(window, GwlStyle);

            var stripped = _originalStyle &
                           ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu |
                             WsBorder | WsDlgFrame);
            SetWindowLong(window, GwlStyle, stripped);

            _target = MonitorRectOf(window);
            _window = window;
            Place();
            return true;
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not make the external window fullscreen", e);
            _window = IntPtr.Zero;
            return false;
        }
    }

    // SWP_FRAMECHANGED is required, not optional: without it Windows keeps drawing the old frame until
    // something else forces a recalculation, and the title bar stays visible over a window that no
    // longer has one.
    private static void Place() =>
        SetWindowPos(_window, HwndTopmost,
            _target.Left, _target.Top,
            _target.Right - _target.Left, _target.Bottom - _target.Top,
            SwpFrameChanged | SwpShowWindow | SwpNoSendChanging);

    /// <summary>
    /// Puts the window back where it belongs if anything moved or resized it, and reports whether it
    /// had to.
    ///
    /// Needed because the title bar a Chromium-based program paints for itself is still draggable
    /// even with no Windows frame left. Drag it and the window walks off the screen - and with no
    /// frame there is nothing left to drag it back by.
    /// </summary>
    public static bool KeepFullscreen()
    {
        if (_window == IntPtr.Zero || !IsWindow(_window) || IsIconic(_window))
        {
            return false;
        }

        if (!GetWindowRect(_window, out var now))
        {
            return false;
        }

        if (now.Left == _target.Left && now.Top == _target.Top &&
            now.Right == _target.Right && now.Bottom == _target.Bottom)
        {
            return false;
        }

        Place();
        return true;
    }

    /// <summary>Gives the window its frame and size back. Safe to call when nothing was changed.</summary>
    public static void Restore()
    {
        var w = _window;
        _window = IntPtr.Zero;

        if (w == IntPtr.Zero || !IsWindow(w))
        {
            return; // already gone: nothing to put back
        }

        try
        {
            SetWindowLong(w, GwlStyle, _originalStyle);

            // HWND_NOTOPMOST matters as much as the size here: leave it out and the program stays
            // pinned above everything for the rest of its life, Playfront included.
            SetWindowPos(w, HwndNoTopmost,
                _originalRect.Left, _originalRect.Top,
                _originalRect.Right - _originalRect.Left,
                _originalRect.Bottom - _originalRect.Top,
                SwpFrameChanged | SwpShowWindow);
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not restore the external window", e);
        }
    }

    /// <summary>
    /// Brings a window of ours to the front, for real.
    ///
    /// Plain SetForegroundWindow is not enough and neither is Avalonia's Activate(): Windows only
    /// lets the process that currently owns the input change the foreground, precisely so background
    /// programs cannot pop up over what you are doing. After driving another program, that process is
    /// the OTHER one - so leaving pointer mode used to put the cursor and the window back correctly
    /// and then leave Playfront sitting behind the program it had just released.
    ///
    /// Attaching to the input queue of whoever is in front makes us count as that owner for the
    /// length of the call. It is the standard way round it, and it must be detached again.
    /// </summary>
    public static void ForceForeground(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window))
        {
            return;
        }

        var attached = false;
        var mine = GetCurrentThreadId();
        uint theirs = 0;

        try
        {
            var fg = GetForegroundWindow();
            if (fg == window)
            {
                return;
            }

            if (fg != IntPtr.Zero)
            {
                theirs = GetWindowThreadProcessId(fg, out _);
                if (theirs != mine)
                {
                    attached = AttachThreadInput(mine, theirs, true);
                }
            }

            // Only when minimised: SW_RESTORE on a maximised window un-maximises it, which would
            // shrink the shell instead of raising it.
            if (IsIconic(window))
            {
                ShowWindow(window, SwRestore);
            }

            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not bring the window to the front", e);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(mine, theirs, false);
            }
        }
    }

    /// <summary>True when the foreground window belongs to the given process id.</summary>
    public static bool ForegroundBelongsTo(int processId)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(fg, out var pid);
        return (int)pid == processId;
    }

    /// <summary>
    /// True when the window in front belongs to a process with this name.
    ///
    /// This is the question that matters for pointer mode, and it took a wrong turn to see it: the
    /// first version asked "is Playfront in front again?", which misses everything else. Minimising
    /// the program hands the foreground to whatever is behind it - which may be neither Playfront nor
    /// the program - and the shell carried on driving a mouse over a window nobody was looking at.
    /// </summary>
    public static bool ForegroundIsProcess(string processName)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return false;
        }

        // MINIMISED COUNTS AS NOT IN FRONT, and this line is the whole point. Windows keeps
        // reporting a minimised window as the foreground one when nothing else took the focus -
        // verified here: IsIconic said true and GetForegroundWindow still named it. Without this
        // check, minimising the program left the shell driving a mouse over a window that was not
        // even on screen.
        if (IsIconic(fg))
        {
            return false;
        }

        GetWindowThreadProcessId(fg, out var pid);

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // the process went away between the two calls
        }
    }

    /// <summary>
    /// Full bounds of the monitor a window sits on, in real pixels. The FULL monitor, not the working
    /// area: anything placed on top of a fullscreen program has to line up with its edges, and the
    /// working area stops short of the taskbar.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) MonitorBounds(IntPtr window)
    {
        var r = MonitorRectOf(window);
        return (r.Left, r.Top, r.Right, r.Bottom);
    }

    private static Rect MonitorRectOf(IntPtr window)
    {
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var mon = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
        {
            return mi.Monitor; // the FULL monitor, not the working area: the taskbar is covered too
        }

        return new Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
