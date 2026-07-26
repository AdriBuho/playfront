using System;
using Playfront.App.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Playfront.App.Views;

/// <summary>
/// Pantalla "System Updates" (Ajustes -> System -> Updates). Se monta bajo demanda y se libera al
/// salir con B, igual que el resto de pantallas del shell.
///
/// La primera casilla es el ESTADO de la actualizacion y manda sobre el resto: al abrir la pantalla
/// se lanza una comprobacion sola, y segun el resultado la casilla se enciende (hay algo que pulsar:
/// descargar, o reiniciar para aplicar) o se apaga (no hay nada que hacer, y el foco la salta).
/// Toda la logica de verdad vive en <see cref="UpdateService"/>; aqui solo se pinta.
///
/// Los dos interruptores de abajo siguen siendo decorativos: se dibujan marcados porque asi salen
/// en la referencia, y todavia no leen ni escriben nada.
/// </summary>
public partial class SystemUpdatesView : UserControl
{
    private const int StatusTile = 0;
    private const int LastTile = 3;

    // Colores medidos en la referencia. La casilla apagada no es un color inventado: es el que tiene
    // "No console update available" en la captura del usuario.
    private static readonly IBrush DisabledBackground = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
    private static readonly IBrush DisabledForeground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
    private static readonly IBrush NormalBackground = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));

    private readonly UpdateService _updates;
    private readonly Border[] _tiles;
    private readonly Border[] _rings;

    // Texto de la derecha para cada casilla. Solo se conoce el de "Latest console update status":
    // es el unico que aparece en la referencia, y aqui no se inventa texto que no se haya medido.
    private static readonly string?[] Descriptions =
    {
        null,
        "See when your console last updated, when it last checked for updates, and what's new in the latest update.",
        null,
        null,
    };

    // Arranca en la segunda casilla, que es donde esta el anillo en la referencia. Si la comprobacion
    // encuentra actualizacion, la primera se enciende pero NO se roba el foco: mover la seleccion
    // sola bajo el dedo del usuario es justo lo que no se espera de una pantalla de ajustes.
    private int _tile = 1;

    /// <summary>Se pide cerrar esta pantalla y volver a Ajustes (B).</summary>
    public event Action? ExitRequested;

    // Interno (no publico) porque recibe UpdateService, que tambien lo es. La vista la construye
    // MainWindow, que vive en el mismo ensamblado.
    internal SystemUpdatesView(UpdateService updates)
    {
        InitializeComponent();

        _updates = updates;
        _tiles = new[] { UpdTile0, UpdTile1, UpdTile2, UpdTile3 };
        _rings = new[] { UpdRing0, UpdRing1, UpdRing2, UpdRing3 };

        _updates.Changed += OnUpdatesChanged;

        UpdateSelection();

        // Comprobacion automatica al entrar. No se comprueba si ya hay una descarga en marcha o
        // esperando reinicio: eso tiraria a la basura lo ya descargado.
        if (_updates.State is UpdateState.Idle or UpdateState.Unsupported
            or UpdateState.UpToDate or UpdateState.Failed)
        {
            _ = _updates.CheckAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Imprescindible: la vista se destruye y se vuelve a crear cada vez que se abre la pantalla.
        // Sin esto, cada visita dejaria una suscripcion viva apuntando a una vista muerta, y el
        // servicio (que vive mientras viva la app) las acumularia todas.
        _updates.Changed -= OnUpdatesChanged;
        base.OnDetachedFromVisualTree(e);
    }

    public void Move(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;

            case GamepadButton.A when _tile == StatusTile:
                ActivateStatusTile();
                return;

            case GamepadButton.Up when _tile > FirstSelectableTile():
                _tile--;
                break;
            case GamepadButton.Down when _tile < LastTile:
                _tile++;
                break;

            default:
                return;
        }

        UpdateSelection();
    }

    // Lo que hace la casilla de estado al pulsarla depende de en que punto esta el proceso. Los
    // estados que no aparecen aqui no son pulsables (ver StatusTileIsActionable).
    private void ActivateStatusTile()
    {
        switch (_updates.State)
        {
            case UpdateState.Available:
                _ = _updates.DownloadAsync();
                break;
            case UpdateState.ReadyToRestart:
                // No vuelve de aqui: el proceso muere y arranca la version nueva.
                _updates.ApplyAndRestart();
                break;
        }
    }

    // Hay algo que pulsar ahora mismo?
    private bool StatusTileIsActionable() =>
        _updates.State is UpdateState.Available or UpdateState.ReadyToRestart;

    // Esta "viva"? Es lo que decide si se ve encendida y si el foco puede estar en ella. Incluye la
    // descarga a proposito, aunque durante la descarga no se pueda pulsar: si no, al pulsar
    // "descargar" el foco se caeria solo a la casilla de abajo y habria que volver a subir para
    // reiniciar. Mientras baja tambien esta ensenando el porcentaje, asi que apagarla seria mentir.
    private bool StatusTileIsLive() =>
        StatusTileIsActionable() || _updates.State is UpdateState.Downloading;

    private int FirstSelectableTile() => StatusTileIsLive() ? StatusTile : 1;

    // El servicio avisa desde el hilo en el que termine la tarea de red, no desde el de la interfaz.
    // Tocar controles de Avalonia desde otro hilo tumba la app, asi que se rebota siempre al hilo
    // de la interfaz antes de pintar nada.
    private void OnUpdatesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateSelection();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateSelection);
        }
    }

    private void UpdateSelection()
    {
        var live = StatusTileIsLive();

        UpdStatusText.Text = StatusText();
        UpdTile0.Background = live ? NormalBackground : DisabledBackground;
        UpdStatusText.Foreground = live ? Brushes.White : DisabledForeground;

        // Si el foco estaba en la casilla de estado y esta ha dejado de ser pulsable (por ejemplo,
        // la comprobacion termina en "ya estas al dia"), hay que sacarlo de ahi: si no, quedaria el
        // anillo sobre una casilla apagada que no responde a nada.
        if (_tile < FirstSelectableTile())
        {
            _tile = FirstSelectableTile();
        }

        for (var i = StatusTile; i <= LastTile; i++)
        {
            var isSelected = i == _tile;
            _tiles[i].Classes.Set("selected", isSelected);
            _rings[i].Classes.Set("selected", isSelected);
            // El anillo de la casilla de estado no debe verse cuando esta apagada.
            _rings[i].IsVisible = i != StatusTile || live;
        }

        // Sin texto medido para esa casilla se deja el hueco vacio en vez de rellenarlo con algo
        // inventado: es preferible que se note que falta a que parezca terminado y sea falso.
        UpdDescription.Text = Descriptions[_tile] ?? string.Empty;
    }

    // Textos en ingles (norma de idioma de la interfaz). Solo "No console update available" sale de
    // la referencia del usuario; los demas son provisionales hasta tener capturas de esos estados.
    private string StatusText() => _updates.State switch
    {
        UpdateState.Idle or UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => "No console update available",
        UpdateState.Available => _updates.AvailableVersion is { } v
            ? $"Console update available ({v})"
            : "Console update available",
        UpdateState.Downloading => $"Downloading update… {_updates.Progress}%",
        UpdateState.ReadyToRestart => "Restart to install update",
        // Frase propia, mas corta que la del servicio: la suya ("Updates are only available when
        // Playfront is installed with its installer.") ocupa tres lineas y no cabe en la casilla.
        // La larga se sigue escribiendo en el registro, que es donde sirve para diagnosticar.
        UpdateState.Unsupported => "Updates only work when Playfront is installed",
        // Aqui si se usa la del servicio tal cual: son frases cortas y ya explican el motivo
        // (sin conexion, disco lleno...) en vez de esconderlo detras de un "error" generico.
        UpdateState.Failed => _updates.LastError ?? "Couldn't check for updates",
        _ => "Checking for updates…",
    };
}
