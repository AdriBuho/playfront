using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Playfront.App.Input;

/// <summary>
/// Moves the mouse from the left stick, on a thread of its own.
///
/// WHY A SEPARATE THREAD, and it is the whole point of this class: the first version rode the same
/// UI timer as menu navigation, and it felt terrible. Two reasons, and both are fatal for a pointer:
///
///   - That timer runs at 20 ticks a second, and even raised to 60 a DispatcherTimer only fires when
///     the UI thread is free. Playfront is decoding a background video on that thread, so the gaps
///     between ticks are uneven - and an uneven gap is exactly what the eye reads as stutter.
///   - Moving in big jumps once per tick looks like teleporting. Smoothness comes from small steps
///     delivered often and regularly.
///
/// Here the loop runs at 240 Hz on a dedicated thread, reads XInput itself and calls SendInput
/// directly. It never touches Avalonia, so nothing the UI does can stall it.
///
/// The movement is time-based, not per-tick: each step is multiplied by how long the last one
/// actually took. A hiccup then produces one slightly longer step instead of a visible stall, and
/// the speed stays the same regardless of what the machine is doing.
/// </summary>
public sealed class PointerDriver
{
    // 240 a second. Far above the screen's refresh on purpose: what matters is that no frame is ever
    // left without a fresh position, and the cost is negligible (an XInput read is a few
    // microseconds).
    private const int HzTarget = 240;

    // XInput's recommended dead zone. Without it the pointer drifts on its own: a stick at rest
    // never reads exactly zero.
    private const double DeadZone = 7849;

    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Pixels per second at full tilt.</summary>
    public double Speed { get; set; } = 1100;

    /// <summary>
    /// How much the response curves. 1 = linear (hard to aim), 2 = squared (precise when nudged,
    /// quick when pushed). Kept adjustable because this is pure feel and only testing settles it.
    /// </summary>
    public double Curve { get; set; } = 2.0;

    /// <summary>Wheel notches per second at full tilt on the right stick.</summary>
    public double ScrollSpeed { get; set; } = 8;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true, // must never keep the process alive
            Name = "Playfront pointer",
            // Above normal so the cursor stays smooth while something heavier is running, but not
            // realtime: starving the rest of the machine to move a pointer would be worse.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread = null; // it is a background thread and checks the flag; no need to join
    }

    private void Loop()
    {
        var reloj = Stopwatch.StartNew();
        var anterior = reloj.Elapsed.TotalSeconds;
        double restoScroll = 0;
        var periodo = 1.0 / HzTarget;

        // The position is kept HERE, in fractions of a pixel, and pushed to Windows as an absolute
        // spot. That is what makes it smooth: asking Windows to "move by n pixels" runs every step
        // through its pointer acceleration and rounds it, and hundreds of tiny steps a second come
        // out uneven. Starts wherever the user's pointer already was.
        var (px, py) = VirtualMouse.Position();
        var (vx, vy, vw, vh) = VirtualMouse.VirtualScreen();

        while (_running)
        {
            var ahora = reloj.Elapsed.TotalSeconds;
            var dt = ahora - anterior;
            anterior = ahora;

            // A pause (the machine was busy, the thread was descheduled) must not turn into one huge
            // jump when it resumes.
            if (dt > 0.1)
            {
                dt = 0.1;
            }

            if (XInputGetState(0, out var state) == 0)
            {
                var (nx, ny) = Curved(state.Gamepad.sThumbLX, state.Gamepad.sThumbLY);
                if (nx != 0 || ny != 0)
                {
                    // Time-based: the distance depends on how long this step really lasted, so the
                    // speed does not change when the loop is delayed.
                    px += nx * Speed * dt;
                    py += -ny * Speed * dt; // stick Y is up-positive, the screen's is down

                    // Kept on screen here rather than letting Windows clamp it: otherwise the
                    // fractional position keeps drifting past the edge and the pointer takes a moment
                    // to come back when the stick is pushed the other way.
                    px = Math.Clamp(px, vx, vx + vw - 1);
                    py = Math.Clamp(py, vy, vy + vh - 1);

                    VirtualMouse.MoveTo(px, py);
                }
                else
                {
                    // Resynced while at rest: anything else on the machine may have moved the
                    // pointer (a real mouse, an app), and starting the next push from a stale
                    // position would make it jump.
                    (px, py) = VirtualMouse.Position();
                }

                var (_, sy) = Curved(state.Gamepad.sThumbRX, state.Gamepad.sThumbRY);
                if (sy != 0)
                {
                    var s = sy * ScrollSpeed * 120 * dt + restoScroll;
                    var isc = (int)s;
                    restoScroll = s - isc;
                    if (isc != 0)
                    {
                        VirtualMouse.Scroll(isc);
                    }
                }
                else
                {
                    restoScroll = 0;
                }
            }

            // Wait out the rest of the period on a HIGH-RESOLUTION timer, never Thread.Sleep.
            //
            // This is what fixes the stutter, and the numbers are worth keeping: Windows' default
            // clock granularity is 15.6 ms, so Thread.Sleep(4) - the wait this loop needs at 240 Hz -
            // actually sleeps 16.68 ms on average and anywhere from 13 to 42. The loop was running at
            // 60 Hz instead of 240, and with gaps varying threefold. Uneven gaps are exactly what the
            // eye reads as jerky movement; a real mouse reports at a perfectly steady rate, which is
            // why the console's own pointer feels smooth.
            var gastado = reloj.Elapsed.TotalSeconds - ahora;
            Esperar(periodo - gastado);
        }

        _timer?.Dispose();
        _timer = null;
    }

    private WaitableTimer? _timer;

    private void Esperar(double segundos)
    {
        if (segundos <= 0)
        {
            return;
        }

        _timer ??= WaitableTimer.Create();

        // Falls back to Sleep only when the high-resolution timer is unavailable. Coarse, but better
        // than spinning a core flat out on a handheld running off a battery.
        if (_timer is null)
        {
            Thread.Sleep(Math.Max(1, (int)(segundos * 1000)));
            return;
        }

        _timer.WaitFor(segundos);
    }

    // Raw stick -> -1..1 with the dead zone removed and the response curved. Rescaled from the edge
    // of the dead zone so the pointer starts from a standstill instead of jumping.
    private (double X, double Y) Curved(short rawX, short rawY)
    {
        double x = rawX, y = rawY;
        var len = Math.Sqrt(x * x + y * y);
        if (len < DeadZone)
        {
            return (0, 0);
        }

        var unit = Math.Min(1.0, (len - DeadZone) / (32767 - DeadZone));
        var factor = Math.Pow(unit, Curve) / len;
        return (x * factor, y * factor);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint index, out XInputState state);
}
