using System;
using System.Runtime.InteropServices;

namespace Playfront.App.Input;

/// <summary>
/// Drives the real Windows mouse, so the controller can operate a window that is not ours - Spotify,
/// which is a separate program Playfront cannot draw on or inject into.
///
/// Everything goes through SendInput, NOT SetCursorPos. SendInput feeds the same queue a physical
/// mouse does, so every application accepts it without knowing the difference; SetCursorPos only
/// teleports the pointer and generates no button or wheel events at all.
/// </summary>
public static class VirtualMouse
{
    private const uint InputMouse = 0;

    private const uint MoveRelative = 0x0001;
    private const uint MoveAbsolute = 0x8000;
    private const uint VirtualDesk = 0x4000;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint Wheel = 0x0800;

    /// <summary>Moves the pointer by a delta in pixels.</summary>
    public static void Move(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        Send(MoveRelative, dx, dy, 0);
    }

    /// <summary>
    /// Puts the pointer at an exact spot on the virtual desktop.
    ///
    /// THIS IS THE ONE TO USE FOR A STICK, and the difference is not subtle. A relative move goes
    /// through Windows' pointer ballistics - "enhanced pointer precision", on by default - which
    /// rescales every delta by how fast it thinks the mouse is going and then rounds to whole pixels.
    /// Feeding it hundreds of tiny deltas a second, as a stick does, means part of each one is
    /// rounded away and what comes out is uneven: the pointer moves in little jumps. Absolute
    /// positioning skips all of that.
    ///
    /// Coordinates are 0..65535 across the whole virtual desktop, so on a 1920-wide screen one step
    /// is about a thirtieth of a pixel - far finer than anything the eye needs.
    /// </summary>
    public static void MoveTo(double x, double y)
    {
        var vx = GetSystemMetrics(SmXVirtualScreen);
        var vy = GetSystemMetrics(SmYVirtualScreen);
        var vw = GetSystemMetrics(SmCxVirtualScreen);
        var vh = GetSystemMetrics(SmCyVirtualScreen);
        if (vw <= 1 || vh <= 1)
        {
            return;
        }

        var nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));

        Send(MoveAbsolute | VirtualDesk | 0x0001 /* MOVE */, Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535), 0);
    }

    /// <summary>Where the pointer is right now, so a session can start from wherever the user left it.</summary>
    public static (double X, double Y) Position()
    {
        return GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
    }

    /// <summary>Size and origin of the whole virtual desktop, for clamping.</summary>
    public static (int X, int Y, int Width, int Height) VirtualScreen()
        => (GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point p);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>A full left click (press and release).</summary>
    public static void Click()
    {
        Send(LeftDown, 0, 0, 0);
        Send(LeftUp, 0, 0, 0);
    }

    /// <summary>A full right click, for context menus.</summary>
    public static void RightClick()
    {
        Send(RightDown, 0, 0, 0);
        Send(RightUp, 0, 0, 0);
    }

    /// <summary>
    /// Wheel scroll. One notch is 120 units, which is what applications expect; smaller values give
    /// the smooth scrolling a stick needs.
    /// </summary>
    public static void Scroll(int amount)
    {
        if (amount != 0)
        {
            Send(Wheel, 0, 0, amount);
        }
    }

    private static void Send(uint flags, int dx, int dy, int data)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = data,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public int MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
