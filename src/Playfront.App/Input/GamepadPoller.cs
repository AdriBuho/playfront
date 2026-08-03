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

    // The Xbox button. Only ever set through the undocumented entry point - see the import below.
    private const ushort ButtonGuide = 0x0400;

    /// <summary>
    /// How often <see cref="Poll"/> is being called, in milliseconds. Whoever owns the timer must
    /// keep this in step with it. It exists because the hold-B threshold is counted in POLLS: the
    /// pointer needs a much faster timer, and without this "hold B" would trigger after a quarter of
    /// a second instead of 800 ms.
    /// </summary>
    public int PollIntervalMs { get; set; } = 50;

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

    // XInput has FOUR slots (0..3) and the pad does not always land on the first one. Anything that
    // presents itself as a controller takes a slot: a virtual pad, a wheel, a second controller left
    // plugged in - and a pad that reconnects usually comes back on a different one. Reading slot 0
    // only leaves the shell dead to the controller with nothing on screen to explain why, which on a
    // shell means an unusable machine.
    private const int MaxSlots = 4;

    // Gap between looks at slots believed EMPTY. That read goes off and enumerates devices, and is
    // the one XInput warns not to do every frame: measured 57.7 us against 6.7 us for a slot with a
    // pad in it. Slots that HAVE a pad are therefore read every poll, which is the point - sampling a
    // second pad a few times a second misses an ordinary button press (~150 ms), so it would only be
    // noticed if its buttons were held down.
    private const int DiscoverMs = 1000;

    private int _activeSlot = -1; // -1 = no pad
    private readonly bool[] _connected = new bool[MaxSlots];
    private int _discoverCountdown; // polls left; 0 on start = look on the very first poll

    /// <summary>Slot the pad in use is on, or -1 when there is none.</summary>
    public int ActiveSlot => _activeSlot;

    /// <summary>
    /// The pad moved to a different slot (or there is no longer one, -1). Anything else that reads
    /// XInput directly - the pointer thread - has to follow, or it ends up reading a slot nobody is
    /// touching.
    /// </summary>
    public event Action<int>? ActiveSlotChanged;

    // Whether accelerating auto-repeat is active. Off by default: normal screens move one step per
    // press. Turned on only where paging through many items fast matters ("Dynamic backgrounds");
    // MainWindow toggles it on entering and leaving that screen.
    public bool RepeatEnabled { get; set; }

    public event Action<GamepadButton>? ButtonPressed;

    /// <summary>Fired at the end of every poll, connected or not. For anything that needs the raw
    /// stick rather than button events - the mouse pointer, which has to move smoothly.</summary>
    public event Action? Polled;

    // Raw stick, -32768..32767, as the pad reports it. Buttons are no use for a pointer: how far the
    // stick is pushed decides how fast it moves.
    public short LeftX { get; private set; }
    public short LeftY { get; private set; }
    public short RightX { get; private set; }
    public short RightY { get; private set; }

    /// <summary>
    /// While true the left stick does NOT act as a d-pad. Set when the pointer is driving an outside
    /// window: otherwise Playfront would keep navigating its own screens behind that window, where
    /// nobody can see what is happening.
    /// </summary>
    public bool PointerMode { get; set; }

    /// <summary>
    /// The Xbox button was pressed. The way back to the shell from anywhere, the same as on a console.
    /// </summary>
    public event Action? GuidePressed;

    public void Poll()
    {
        XInputState state = default;
        var haveState = false;

        // 1. The pad in use, every poll.
        if (_activeSlot >= 0)
        {
            haveState = XInputGetState(_activeSlot, out state) == 0;
            _connected[_activeSlot] = haveState;
            if (!haveState)
            {
                SetActiveSlot(-1);
                _discoverCountdown = 0; // unplugged: go looking on this same poll
            }
        }

        // 2. Every OTHER pad already known to be plugged in, also every poll - cheap, and the only
        //    sampling rate at which a normal button press is not missed.
        for (var slot = 0; slot < MaxSlots; slot++)
        {
            if (slot == _activeSlot || !_connected[slot])
            {
                continue;
            }

            if (XInputGetState(slot, out var other) != 0)
            {
                _connected[slot] = false;
                continue;
            }

            // Being plugged in is not enough to take over. A pad left on a table would grab the
            // input in the middle of navigating with the one in hand, which is worse than the bug
            // this replaces - so it has to be BEING USED.
            if (_activeSlot < 0 || IsBeingUsed(other.Gamepad))
            {
                SetActiveSlot(slot);
                state = other;
                haveState = true;
            }
        }

        // 3. Slots believed empty: the slow read, about once a second.
        if (--_discoverCountdown <= 0)
        {
            _discoverCountdown = Math.Max(1, DiscoverMs / Math.Max(1, PollIntervalMs));

            for (var slot = 0; slot < MaxSlots; slot++)
            {
                if (_connected[slot] || XInputGetState(slot, out var found) != 0)
                {
                    continue;
                }

                _connected[slot] = true;

                // Only adopted right away when there is nothing else; a pad appearing mid-session
                // waits until someone presses something on it (step 2).
                if (_activeSlot < 0)
                {
                    SetActiveSlot(slot);
                    state = found;
                    haveState = true;
                }
            }
        }

        IsConnected = haveState;

        if (!IsConnected)
        {
            _previousButtons = 0;
            _repeatButton = null;
            _prevLT = false;
            _prevRT = false;
            LeftX = LeftY = RightX = RightY = 0;
            Polled?.Invoke();
            return;
        }

        LeftX = state.Gamepad.sThumbLX;
        LeftY = state.Gamepad.sThumbLY;
        RightX = state.Gamepad.sThumbRX;
        RightY = state.Gamepad.sThumbRY;

        var buttons = state.Gamepad.wButtons;

        // In pointer mode the stick MOVES the pointer, so it must not also count as a direction.
        if (!PointerMode)
        {
            if (state.Gamepad.sThumbLY > StickThreshold) buttons |= DPadUp;
            if (state.Gamepad.sThumbLY < -StickThreshold) buttons |= DPadDown;
            if (state.Gamepad.sThumbLX < -StickThreshold) buttons |= DPadLeft;
            if (state.Gamepad.sThumbLX > StickThreshold) buttons |= DPadRight;
        }

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

        // THE XBOX BUTTON. Rising edge, and handled apart from every other button: it does not
        // navigate the screen you are on, it goes home from wherever you are - including from an
        // outside program that has the whole display. It replaced holding B, which had to be
        // explained on screen to be usable at all.
        if ((buttons & ButtonGuide) != 0 && (_previousButtons & ButtonGuide) == 0)
        {
            GuidePressed?.Invoke();
        }

        // Directions: rising edge, plus accelerating auto-repeat while held.
        HandleDirections(buttons, pressedNow);

        _previousButtons = buttons;
        Polled?.Invoke();
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

    private void SetActiveSlot(int slot)
    {
        if (_activeSlot == slot)
        {
            return;
        }

        _activeSlot = slot;

        // Start clean on the new pad: nothing it holds was ever seen before, so whatever is down now
        // must read as a fresh press. That press is normally the very one that caused the handover,
        // and swallowing it would make the second pad feel like it needs a button pressed twice.
        _previousButtons = 0;
        _repeatButton = null;
        _prevLT = false;
        _prevRT = false;

        ActiveSlotChanged?.Invoke(slot);
    }

    // "Somebody is using this pad." Deliberately blunt: any button, a trigger past the same pull the
    // rest of the app uses, or a stick past half way. A worn stick's drift never reaches that, so a
    // pad left face down on a table cannot steal the input from the one in hand.
    private static bool IsBeingUsed(in XInputGamepad pad) =>
        pad.wButtons != 0
        || pad.bLeftTrigger > TriggerThreshold
        || pad.bRightTrigger > TriggerThreshold
        || Math.Abs((int)pad.sThumbLX) > StickThreshold
        || Math.Abs((int)pad.sThumbLY) > StickThreshold
        || Math.Abs((int)pad.sThumbRX) > StickThreshold
        || Math.Abs((int)pad.sThumbRY) > StickThreshold;

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

    // ORDINAL 100, not the documented XInputGetState. Same signature, one difference that matters
    // here: the public entry point deliberately MASKS OUT the Guide button (the Xbox one), and this
    // undocumented twin reports it in bit 0x0400. There is no supported way to read that button, and
    // without it there is nothing to return to the shell with.
    //
    // Verified on this machine: pressing Guide sets the bit through this call and never through the
    // public one. It has been present in every xinput1_3/1_4 Windows has shipped.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
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
