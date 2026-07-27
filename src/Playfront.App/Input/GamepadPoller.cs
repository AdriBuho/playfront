using System;
using System.Runtime.InteropServices;

namespace Playfront.App.Input;

public enum GamepadButton
{
    Up,
    Down,
    Left,
    Right,
    A,
    B,
    LB, // Left Shoulder
    RB, // Right Shoulder
    X,
    Y,
    Start,
    Back, // the "View"/Select button
    LT,   // Left Trigger (analog)
    RT,   // Right Trigger (analog)
}

public sealed class GamepadPoller
{
    private const ushort DPadUp = 0x0001;
    private const ushort DPadDown = 0x0002;
    private const ushort DPadLeft = 0x0004;
    private const ushort DPadRight = 0x0008;
    private const ushort ButtonA = 0x1000;
    private const ushort ButtonB = 0x2000;
    private const ushort ButtonX = 0x4000;
    private const ushort ButtonY = 0x8000;
    private const ushort ButtonLB = 0x0100; // Left Shoulder
    private const ushort ButtonRB = 0x0200; // Right Shoulder
    private const ushort ButtonStart = 0x0010;
    private const ushort ButtonBack = 0x0020; // View/Select button

    // Hold-B: how many polls (Poll runs every ~50 ms) B must stay down to count as HELD rather than a
    // tap. 16 x 50 ms = 800 ms. Used, for instance, to leave the YouTube screen (tap B = back, hold =
    // exit). The event fires ONCE per long press.
    private const int BHoldPolls = 16;
    private int _bHeldPolls;
    private bool _bHoldFired;

    // The stick is treated as D-pad buttons: tilted past this threshold on an axis counts as that
    // direction being pressed.
    private const short StickThreshold = 16000;

    // Triggers are analog (0..255); past this pull they count as pressed. 64 (~25%) demands a deliberate
    // pull to avoid accidental fires, without needing them bottomed out.
    private const byte TriggerThreshold = 64;
    private bool _prevLT;
    private bool _prevRT;

    private ushort _previousButtons;

    // ACCELERATING auto-repeat while a direction is held (D-pad/stick), counted in polls (Poll runs
    // every 50 ms). On hold: the first press fires instantly, then nothing for the delay (~350 ms),
    // then repeats that speed up - the gap shrinks each time down to the minimum, so long lists move
    // fast but smoothly. A/B do NOT repeat (rising edge only).
    private const int HoldDelayPolls = 7;      // ~350 ms held before repeating starts
    private const int RepeatStartPolls = 5;    // first repeat interval (~250 ms)
    private const int RepeatMinPolls = 1;      // floor (~50 ms = fastest)
    private GamepadButton? _repeatButton;      // direction currently held (or null)
    private int _repeatPolls;                  // polls since the direction went down
    private int _repeatInterval;               // current interval (shrinks = accelerates)
    private int _repeatNextPoll;               // poll at which the next repeat is due

    public bool IsConnected { get; private set; }

    // Whether accelerating auto-repeat is active. Off by default: normal screens move one step per
    // press. Turned on only where paging through many items fast matters ("Dynamic backgrounds");
    // MainWindow toggles it on entering and leaving that screen.
    public bool RepeatEnabled { get; set; }

    public event Action<GamepadButton>? ButtonPressed;

    // B HELD (~800 ms). Distinct from ButtonPressed(B), which is the TAP (rising edge). Used by the
    // YouTube screen: tap = back, hold = exit.
    public event Action? BHeld;

    public void Poll()
    {
        var result = XInputGetState(0, out var state);
        IsConnected = result == 0;

        if (!IsConnected)
        {
            _previousButtons = 0;
            _repeatButton = null;
            _bHeldPolls = 0;
            _bHoldFired = false;
            _prevLT = false;
            _prevRT = false;
            return;
        }

        var buttons = state.Gamepad.wButtons;

        if (state.Gamepad.sThumbLY > StickThreshold) buttons |= DPadUp;
        if (state.Gamepad.sThumbLY < -StickThreshold) buttons |= DPadDown;
        if (state.Gamepad.sThumbLX < -StickThreshold) buttons |= DPadLeft;
        if (state.Gamepad.sThumbLX > StickThreshold) buttons |= DPadRight;

        // Rising edge only (was up, is now down); otherwise holding a button would move the selection
        // on every poll.
        var pressedNow = (ushort)(buttons & ~_previousButtons);

        // Buttons (not directions): rising edge only, no auto-repeat.
        if ((pressedNow & ButtonA) != 0) ButtonPressed?.Invoke(GamepadButton.A);
        if ((pressedNow & ButtonB) != 0) ButtonPressed?.Invoke(GamepadButton.B);
        if ((pressedNow & ButtonX) != 0) ButtonPressed?.Invoke(GamepadButton.X);
        if ((pressedNow & ButtonY) != 0) ButtonPressed?.Invoke(GamepadButton.Y);
        if ((pressedNow & ButtonLB) != 0) ButtonPressed?.Invoke(GamepadButton.LB);
        if ((pressedNow & ButtonRB) != 0) ButtonPressed?.Invoke(GamepadButton.RB);
        if ((pressedNow & ButtonStart) != 0) ButtonPressed?.Invoke(GamepadButton.Start);
        if ((pressedNow & ButtonBack) != 0) ButtonPressed?.Invoke(GamepadButton.Back);

        // Triggers (analog): count as a button past the threshold; rising edge only (one fire per
        // pull), like A/B.
        var ltDown = state.Gamepad.bLeftTrigger > TriggerThreshold;
        var rtDown = state.Gamepad.bRightTrigger > TriggerThreshold;
        if (ltDown && !_prevLT) ButtonPressed?.Invoke(GamepadButton.LT);
        if (rtDown && !_prevRT) ButtonPressed?.Invoke(GamepadButton.RT);
        _prevLT = ltDown;
        _prevRT = rtDown;

        // B HELD: counts polls with B down; past the threshold, fires BHeld exactly once.
        if ((buttons & ButtonB) != 0)
        {
            _bHeldPolls++;
            if (_bHeldPolls >= BHoldPolls && !_bHoldFired)
            {
                _bHoldFired = true;
                BHeld?.Invoke();
            }
        }
        else
        {
            _bHeldPolls = 0;
            _bHoldFired = false;
        }

        // Directions: rising edge, plus accelerating auto-repeat while held.
        HandleDirections(buttons, pressedNow);

        _previousButtons = buttons;
    }

    private void HandleDirections(ushort buttons, ushort pressedNow)
    {
        // Direction just pressed: fire it and (re)start auto-repeat on it.
        var justPressed = DirectionOf(pressedNow);
        if (justPressed is { } jp)
        {
            ButtonPressed?.Invoke(jp);
            _repeatButton = jp;
            _repeatPolls = 0;
            _repeatInterval = RepeatStartPolls;
            _repeatNextPoll = HoldDelayPolls;
            return;
        }

        // Is the repeating direction still held? (repeat only where it is enabled)
        if (_repeatButton is { } rb)
        {
            if (!RepeatEnabled || (buttons & MaskOf(rb)) == 0)
            {
                _repeatButton = null; // released, or auto-repeat disabled
                return;
            }

            _repeatPolls++;
            if (_repeatPolls >= _repeatNextPoll)
            {
                ButtonPressed?.Invoke(rb);
                _repeatNextPoll = _repeatPolls + _repeatInterval;
                _repeatInterval = Math.Max(RepeatMinPolls, _repeatInterval - 1); // accelerate
            }
        }
    }

    // First direction present in the mask (or null). D-pad/stick only, not A/B.
    private static GamepadButton? DirectionOf(ushort mask)
    {
        if ((mask & DPadUp) != 0) return GamepadButton.Up;
        if ((mask & DPadDown) != 0) return GamepadButton.Down;
        if ((mask & DPadLeft) != 0) return GamepadButton.Left;
        if ((mask & DPadRight) != 0) return GamepadButton.Right;
        return null;
    }

    private static ushort MaskOf(GamepadButton button) => button switch
    {
        GamepadButton.Up => DPadUp,
        GamepadButton.Down => DPadDown,
        GamepadButton.Left => DPadLeft,
        GamepadButton.Right => DPadRight,
        _ => 0,
    };

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, out XInputState pState);

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
}
