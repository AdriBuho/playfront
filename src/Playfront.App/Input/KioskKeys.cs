using System;
using System.Runtime.InteropServices;

namespace Playfront.App.Input;

/// <summary>
/// Swallows the Windows shortcuts that would take the user out of a program Playfront is driving,
/// so the only ways out are the ones the shell knows about.
///
/// The point is not to trap anyone. It is that every OTHER exit leaves the two sides disagreeing:
/// Alt+Tab hands the screen to something else while the controller is still driving a mouse, and
/// the shell finds out a second later from a timer, if at all. Routing every exit through us means
/// the pointer, the system cursor and the other program's window are always put back properly.
///
/// WHAT STAYS WORKING ON PURPOSE, because a shell that can be made unusable is worse than one that
/// can be escaped:
///   - Escape, which is handed to the shell as the keyboard twin of holding B. Hold it and pointer
///     mode ends the same way. This is the guaranteed way out on a machine with no controller.
///   - Ctrl+Shift+Esc (Task Manager) and Ctrl+Alt+Del. The second one cannot be blocked by anything
///     short of a driver anyway; the first one is deliberately left alone as the rescue path.
/// </summary>
public static class KioskKeys
{
    private const int WhKeyboardLl = 13;

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private const int VkTab = 0x09;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkEscape = 0x1B;
    private const int VkZero = 0x30;
    private const int VkQ = 0x51;
    private const int VkNumpad0 = 0x60;
    private const int VkAdd = 0x6B;
    private const int VkSubtract = 0x6D;
    private const int VkOemPlus = 0xBB;
    private const int VkOemMinus = 0xBD;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkF1 = 0x70;
    private const int VkF4 = 0x73;

    private const int LlkhfAltDown = 0x20;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private static IntPtr _hook;

    // Held in a field, and it MUST be: hand a delegate to Windows and let the only reference go out
    // of scope, and the collector frees it while Windows still has the pointer. The process then dies
    // on the next keystroke, far from the code that caused it.
    private static HookProc? _proc;

    private static Action? _onExitKeyDown;
    private static Action? _onExitKeyUp;

    public static bool Active => _hook != IntPtr.Zero;

    // DO NOT INJECT KEYSTROKES FROM HERE. It was tried - a Ctrl+0 sent to the driven program right
    // after resizing it, to force its zoom back to 100% - and it hung Spotify twice out of two, with
    // Windows putting up "the application is not responding". The reason is the hook itself: while it
    // is installed, every keystroke in the system waits on THIS thread, and this thread was blocked
    // inside SetWindowPos waiting on Spotify to finish resizing. Each one waiting for the other.
    // Nothing here should send input; the shell only ever swallows it.

    /// <summary>
    /// Starts swallowing. Must be called from the UI thread: a low-level hook is served on the thread
    /// that installed it, and a thread with no message pump never runs the callback.
    /// </summary>
    public static void Enable(Action onExitKeyDown, Action onExitKeyUp)
    {
        if (Active)
        {
            return;
        }

        _onExitKeyDown = onExitKeyDown;
        _onExitKeyUp = onExitKeyUp;
        _proc = Callback;

        try
        {
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                CrashLog.Info("Could not install the keyboard hook; the Windows shortcuts stay live");
            }
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not install the keyboard hook", e);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>Stops swallowing. Safe to call when it was never started.</summary>
    public static void Disable()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _proc = null;
        _onExitKeyDown = null;
        _onExitKeyUp = null;
    }

    private static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        try
        {
            var info = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
            var msg = (int)wParam;
            var down = msg is WmKeyDown or WmSysKeyDown;
            var alt = (info.Flags & LlkhfAltDown) != 0;
            var ctrl = (GetKeyState(VkControl) & 0x8000) != 0;
            var shift = (GetKeyState(VkShift) & 0x8000) != 0;

            switch (info.VirtualKey)
            {
                // The Windows key on its own opens Start, and every Win+<something> goes somewhere
                // else entirely. Swallowing the key itself covers the whole family at once.
                case VkLWin:
                case VkRWin:
                    return Swallow;

                case VkTab when alt: // Alt+Tab
                case VkF4 when alt:  // Alt+F4, which would close the program from under us
                    return Swallow;

                // Shortcuts the driven program brings of its own. Spotify quits on Ctrl+Shift+Q and
                // opens its help site in a BROWSER on F1 - a whole window landing on top of a shell
                // that is meant to own the screen. Found by reading its menus, not by guessing.
                case VkQ when ctrl && shift:
                case VkF1:
                    return Swallow;

                // Zoom, and this one is not about escaping. Spotify's own zoom changes the HEIGHT of
                // its title bar - measured 64 px at 100%, 51 at two steps out and 80 at two steps in -
                // and the cover over its window buttons is sized to that bar. Letting the zoom move
                // while we are covering it would leave the cover the wrong size.
                case VkOemPlus when ctrl:
                case VkOemMinus when ctrl:
                case VkAdd when ctrl:
                case VkSubtract when ctrl:
                case VkZero when ctrl:
                case VkNumpad0 when ctrl:
                    return Swallow;

                case VkEscape when ctrl && shift:
                    break; // Task Manager: left alone on purpose, see the class comment

                case VkEscape when ctrl: // Ctrl+Esc opens Start
                case VkEscape when alt:  // Alt+Esc cycles windows
                    return Swallow;

                // Plain Escape is not blocked, it is REDIRECTED: the shell treats it exactly like the
                // controller's B, hold included. Swallowed as well so the program underneath does not
                // also act on it.
                case VkEscape:
                    if (down)
                    {
                        _onExitKeyDown?.Invoke();
                    }
                    else
                    {
                        _onExitKeyUp?.Invoke();
                    }

                    return Swallow;
            }
        }
        catch (Exception e)
        {
            // Never let this throw. An exception inside a hook callback takes the process with it, and
            // the process here is the shell.
            CrashLog.Log("Failure inside the keyboard hook", e);
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // Any non-zero return stops the key reaching anyone else.
    private static IntPtr Swallow => new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookStruct
    {
        public int VirtualKey;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
