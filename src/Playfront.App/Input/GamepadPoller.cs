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
    LB, // bumper izquierdo (Left Shoulder)
    RB, // bumper derecho (Right Shoulder)
    X,
    Y,
    Start,
    Back, // el boton "View"/Select del mando
    LT,   // gatillo izquierdo (Left Trigger, analogico)
    RT,   // gatillo derecho (Right Trigger, analogico)
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
    private const ushort ButtonBack = 0x0020; // boton View/Select

    // "Mantener" B: numero de sondeos (Poll cada ~50 ms) que hay que tener B pulsado para que cuente como
    // MANTENIDO en vez de toque. 16 x 50 ms = 800 ms. Se usa, p.ej., para salir de la pantalla de YouTube
    // (toque de B = atras; mantenido = salir). El evento se dispara UNA vez por pulsacion larga.
    private const int BHoldPolls = 16;
    private int _bHeldPolls;
    private bool _bHoldFired;

    // El stick se trata como si fueran botones de cruceta: si se inclina más allá
    // de este umbral en un eje, cuenta como "pulsado" en esa dirección.
    private const short StickThreshold = 16000;

    // Los gatillos son analogicos (0..255). A partir de este apriete cuentan como "pulsado". 64 (~25%)
    // pide un tiron claro para evitar disparos accidentales, sin tener que apretarlos a fondo.
    private const byte TriggerThreshold = 64;
    private bool _prevLT;
    private bool _prevRT;

    private ushort _previousButtons;

    // Auto-repeticion con ACELERACION al mantener una direccion pulsada (cruceta/stick). Se cuenta en
    // "sondeos" (Poll se llama cada 50 ms). Al mantener: 1er toque instantaneo, luego nada durante el
    // retardo (~350 ms), y despues repite acelerando: el intervalo entre repeticiones se acorta cada
    // vez (lento -> rapido) hasta el minimo, para pasar los fondos deprisa pero de forma suave. A/B NO
    // repiten (solo flanco de subida).
    private const int HoldDelayPolls = 7;      // ~350 ms mantenido antes de empezar a repetir
    private const int RepeatStartPolls = 5;    // 1er intervalo de repeticion (~250 ms)
    private const int RepeatMinPolls = 1;      // intervalo minimo (~50 ms = mas rapido)
    private GamepadButton? _repeatButton;      // direccion que se esta manteniendo (o null)
    private int _repeatPolls;                  // sondeos desde que se pulso la direccion
    private int _repeatInterval;               // intervalo actual (va bajando = acelera)
    private int _repeatNextPoll;               // sondeo en el que toca la proxima repeticion

    public bool IsConnected { get; private set; }

    // Si la auto-repeticion con aceleracion esta activa. Por defecto NO (las pantallas normales siguen
    // como antes: un movimiento por pulsacion). Se enciende solo donde interesa pasar muchos elementos
    // rapido (la pantalla de "Dynamic backgrounds"). Lo controla MainWindow al entrar/salir de ella.
    public bool RepeatEnabled { get; set; }

    public event Action<GamepadButton>? ButtonPressed;

    // B MANTENIDO (~800 ms). Distinto de ButtonPressed(B), que es el TOQUE (flanco de subida). Lo usa la
    // pantalla de YouTube: toque = atras, mantenido = salir.
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

        // Solo dispara en el flanco de subida (botón que no estaba pulsado y ahora sí),
        // si no, mantener el botón pulsado movería la selección en cada sondeo.
        var pressedNow = (ushort)(buttons & ~_previousButtons);

        // Botones (no direcciones): solo flanco de subida (sin auto-repeat).
        if ((pressedNow & ButtonA) != 0) ButtonPressed?.Invoke(GamepadButton.A);
        if ((pressedNow & ButtonB) != 0) ButtonPressed?.Invoke(GamepadButton.B);
        if ((pressedNow & ButtonX) != 0) ButtonPressed?.Invoke(GamepadButton.X);
        if ((pressedNow & ButtonY) != 0) ButtonPressed?.Invoke(GamepadButton.Y);
        if ((pressedNow & ButtonLB) != 0) ButtonPressed?.Invoke(GamepadButton.LB);
        if ((pressedNow & ButtonRB) != 0) ButtonPressed?.Invoke(GamepadButton.RB);
        if ((pressedNow & ButtonStart) != 0) ButtonPressed?.Invoke(GamepadButton.Start);
        if ((pressedNow & ButtonBack) != 0) ButtonPressed?.Invoke(GamepadButton.Back);

        // Gatillos (analogicos): cuentan como boton al superar el umbral; solo flanco de subida (un
        // disparo por tiron), como A/B.
        var ltDown = state.Gamepad.bLeftTrigger > TriggerThreshold;
        var rtDown = state.Gamepad.bRightTrigger > TriggerThreshold;
        if (ltDown && !_prevLT) ButtonPressed?.Invoke(GamepadButton.LT);
        if (rtDown && !_prevRT) ButtonPressed?.Invoke(GamepadButton.RT);
        _prevLT = ltDown;
        _prevRT = rtDown;

        // B MANTENIDO: cuenta sondeos con B pulsado; al pasar el umbral, dispara BHeld una sola vez.
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

        // Direcciones: flanco de subida + auto-repeticion con aceleracion al mantener.
        HandleDirections(buttons, pressedNow);

        _previousButtons = buttons;
    }

    private void HandleDirections(ushort buttons, ushort pressedNow)
    {
        // ¿Se acaba de pulsar una direccion? -> dispararla y (re)arrancar el auto-repeat con ella.
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

        // ¿Sigue mantenida la direccion que estaba repitiendo? (solo repite donde esta habilitado)
        if (_repeatButton is { } rb)
        {
            if (!RepeatEnabled || (buttons & MaskOf(rb)) == 0)
            {
                _repeatButton = null; // soltada, o auto-repeat deshabilitado
                return;
            }

            _repeatPolls++;
            if (_repeatPolls >= _repeatNextPoll)
            {
                ButtonPressed?.Invoke(rb);
                _repeatNextPoll = _repeatPolls + _repeatInterval;
                _repeatInterval = Math.Max(RepeatMinPolls, _repeatInterval - 1); // acelera
            }
        }
    }

    // Primera direccion presente en la mascara (o null). Solo cruceta/stick, no A/B.
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
