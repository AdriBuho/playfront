using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Playfront.App.Input;
using Playfront.App.System;
using Playfront.App.Video;
using Playfront.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Playfront.App;

public partial class MainWindow : Window
{
    private static readonly string[] NavLabels = { "My games & apps", "Store", "Game Pass", "Search", "Settings" };

    // Ancho completo (100%) de la barra de relleno de la bateria, en las unidades del lienzo
    // de diseño del icono (ver Rectangle "BatteryFill" en el XAML) - tiene que coincidir con
    // el Width de ese Rectangle. Deliberadamente se mete 1 unidad por debajo del contorno en
    // vez de terminar justo en su borde interior, para que no se vean huecos entre relleno y
    // contorno en pantallas con distinto factor de escala/DPI (ver comentario en el XAML).
    private const double BatteryFillMaxWidth = 20;
    // El color de la barra de bateria ya no depende del estado: va SIEMPRE en verde (peticion del
    // usuario). El verde es fijo (BatteryFill.Fill en el XAML, {StaticResource AccentBrush}), asi que
    // aqui ya no se calcula ningun color - antes se ponia blanca normal, naranja por debajo del 20%
    // y verde al cargar; ahora es verde en todos los casos.

    // Cuanto tiempo minimo se ve la pantalla de carga de Ajustes (engranaje sobre negro),
    // aunque la pantalla real este lista antes. Sin este minimo, si Ajustes carga instantaneo
    // el loading parpadearia un frame y se veria como un glitch en vez de una transicion.
    private const int MinSettingsLoadingMilliseconds = 300;

    // Debe coincidir con la Duration del DoubleTransition de Opacity de SettingsLoadingScreen
    // en el XAML - se usa para esperar a que el fundito de salida termine antes de ocultar
    // el Grid del todo (si no, desaparecería de golpe a mitad del fundido).
    private static readonly TimeSpan SettingsLoadingFadeDuration = TimeSpan.FromMilliseconds(300);

    private readonly Border[][] _rows;

    // Anillo de seleccion de cada casilla de la home (el mismo Border.selectionRing que usan
    // Ajustes y Personalization). Mismos indices que _rows: _homeRings[r][c] rodea a _rows[r][c].
    // La fila 0 (navegacion) no tiene - usa su propio estilo de circulo iluminado.
    private readonly Border[][] _homeRings;
    private readonly double[][] _rowCenters;

    // Anillo verde de seleccion de cada tarjeta/casilla (ver Border.selectionRing en el XAML).
    // Va en un elemento aparte superpuesto y no en el borde de la propia tarjeta porque se dibuja
    // por FUERA de ella, separado unos pixeles. Mismos indices que _personalizationTiles: cada
    // anillo enciende con la tarjeta que rodea.
    private readonly Border[] _personalizationTiles;
    private readonly Border[] _personalizationRings;

    // Tarjetas y anillos de "My color & theme" (My color / System theme).
    private readonly Border[] _colorThemeCards;
    private readonly Border[] _colorThemeRings;
    private readonly GamepadPoller _gamepad = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly BatteryMonitor _battery = new();
    private HardwareVideoBackgroundControl? _videoBackground;

    private int _row;
    private int _col;

    // Estado de la pantalla de Ajustes (ver Border.settingsNavItem/settingsCard en el XAML) -
    // independiente de _row/_col de la home, para que al volver atras la home se acuerde de
    // donde estabas.
    private bool _inSettings;

    // True mientras el velo negro de entrada/salida de Ajustes esta en pantalla, para que
    // pulsar A/B a destiempo no dispare una segunda transicion encima de la que ya corre.
    private bool _settingsTransitioning;

    // La vista de Ajustes se crea BAJO DEMANDA al entrar (EnterSettings) y se libera al salir
    // (ExitSettings); es null cuando no estamos en Ajustes. Su navegacion y estado viven en ella.
    private SettingsView? _settingsView;

    // Pantalla "System Updates" (Ajustes > System > Updates): cuelga de Ajustes, se monta bajo
    // demanda al abrirla y se libera al volver con B. Mientras esta puesta, el mando es suyo.
    private bool _inUpdates;
    private SystemUpdatesView? _updatesView;

    // Estado de la TIENDA (pantalla completa opaca que tapa la home, como Ajustes). La vista se crea
    // BAJO DEMANDA al entrar (EnterStore) y se libera al salir (ExitStore). Mientras esta abierta el
    // mando la navega a ella (ver Move) y el video de la home se pausa (ver IsHomeCovered).
    private bool _inStore;
    private StoreView? _storeView;

    // Estado de la BIBLIOTECA ("My games & apps"): pantalla completa opaca que tapa la home (como
    // Ajustes/Tienda). La vista se crea BAJO DEMANDA al entrar (EnterLibrary) y se libera al salir
    // (ExitLibrary). Por ahora es solo visual: el mando dentro de ella solo hace "atras" con B.
    private bool _inLibrary;
    private LibraryView? _libraryView;

    // Estado de "General Personalization" (ver PersonalizationScreen en el XAML). Es una pantalla
    // completa que tapa la de Ajustes, no un panel dentro de ella, asi que lleva su propio estado
    // aparte: mientras esta abierta, el mando la navega a ella y no a la rejilla de tarjetas de
    // detras. Al cerrarla con B, Ajustes sigue exactamente como estaba.
    private bool _inPersonalization;
    private int _personalizationIndex;

    // Estado de la pantalla "My color & theme" (cuelga de Personalization, ver ColorThemeScreen en
    // el XAML). Como esta encima de Personalization, se comprueba antes que ella en Move().
    private bool _inColorTheme;
    private int _colorThemeIndex;

    // Estado del selector de color (cuelga de "My color & theme", ver ColorPickerScreen). Indices
    // 0..13 = los 14 recuadros (0..6 fila 1, 7..13 fila 2), 14 = boton OK.
    private bool _inColorPicker;
    private int _colorPickerIndex;
    private readonly Border[] _colorSwatchRings = new Border[14];

    // Marca de "aplicado" (triangulo blanco + check) que se coloca sobre el recuadro cuyo color es
    // el acento actual. Una sola, reutilizada.
    private Canvas? _appliedCheck;

    // Hex del acento del tema actualmente aplicado (el color de las selecciones). Se carga de disco
    // al arrancar y cambia al elegir un color en el selector. AccentTheme ya lo aplico a los recursos
    // antes de crear la ventana; aqui solo se guarda para saber cual resaltar al abrir el selector.
    private string _currentAccentHex = AccentTheme.DefaultHex;

    // Los 14 colores del selector, calcados PIXEL A PIXEL del centro de cada recuadro de "2.png"
    // (los colores exactos de la captura). Fila 1 luego fila 2.
    private static readonly string[] ColorSwatchHexes =
    {
        // Ordenados de MAS CLARO a MAS OSCURO (blanco arriba-izq); MISMO orden que AccentTheme.Palette.
        "#FFFFFF", "#DB5985", "#5AA029", "#D84F1F", "#A64AB3", "#207EBB", "#7552A1",
        "#23807F", "#2073C7", "#217F72", "#D01F2F", "#B21F75", "#207A1F", "#991F30",
    };

    // Estado de la pantalla "My background" (cuelga de Personalization, ver MyBackgroundScreen en el
    // XAML). Como esta encima de Personalization, se comprueba antes que ella en Move(). Indices
    // 0..4 = las 5 fuentes de fondo de la columna izquierda, 5 = boton "Restore default background".
    private bool _inMyBackground;
    private int _myBackgroundIndex;
    // Con el foco en el boton "Restore" (indice 5), a que casilla de la izquierda vuelve la flecha
    // Izquierda: la ultima en la que estuvo el foco antes de saltar al boton (no siempre la 0).
    private int _myBackgroundLeftReturn;
    // Cual de las 5 fuentes es el fondo ACTIVO (lleva el triangulo del check). Por defecto 4 =
    // "Dynamic backgrounds", que es lo que Playfront muestra ahora mismo.
    private int _myBackgroundActiveIndex = 4;
    private readonly Border[] _myBackgroundTiles;
    private readonly Border[] _myBackgroundRings;
    // Marca de "fondo activo": triangulo blanco + check en la esquina superior derecha de la casilla
    // activa. Una sola, reutilizada (misma idea que _appliedCheck del selector de color).
    private Canvas? _myBackgroundCheck;

    // Geometria de las casillas de My background (= la de Personalization). Se usa tanto en el XAML
    // como para colocar el triangulo del check.
    private const double MbTileLeft = 108;
    private const double MbTileWidth = 440;
    private const double MbTileTop0 = 265;
    private const double MbTilePitch = 114;

    // Estado del selector "Solid colors" (cuelga de My background, ver SolidColorsScreen en el
    // XAML). Indices 0..13 = los 14 recuadros (0..6 fila 1, 7..13 fila 2), 14 = boton OK.
    private bool _inSolidColors;
    private int _solidColorsIndex;
    private readonly Border[] _solidSwatchRings = new Border[14];
    // Marca de "aplicado" (triangulo blanco + check) sobre el recuadro cuyo color es el fondo actual.
    private Canvas? _solidAppliedCheck;

    // Fondo de la home: null = video dinamico (por defecto); un hex = ese color solido. Se carga de
    // disco al arrancar (BackgroundSettings) y cambia al elegir un color en el selector o pulsar
    // "Restore default background".
    private string? _backgroundSolidHex;

    // Video dinamico concreto elegido en "Dynamic backgrounds" (ruta relativa a Assets/Backgrounds), o
    // null = el fondo por defecto (el primero de la biblioteca, ver DefaultBackground). Solo aplica
    // cuando NO hay color solido activo.
    private string? _backgroundVideoRelPath;

    // Geometria de la rejilla de "Solid colors" (medida del frame, ver el XAML). Se usa para colocar
    // los recuadros, sus anillos y la marca de aplicado.
    private const double SolidSwatchW = 244;
    private const double SolidSwatchH = 209;
    private const double SolidColX0 = 100;
    private const double SolidColPitch = 260;
    private const double SolidRow0Y = 307;
    private const double SolidRow1Y = 532;

    // Estado de "Custom image" (cuelga de My background). Es una pantalla SOLO VISUAL (placeholder del
    // selector de archivos de Windows), asi que no lleva indice ni seleccion interna: se abre y se
    // cierra con B.
    private bool _inCustomImage;

    // Estado de "Dynamic backgrounds" (cuelga de My background). Por ahora es SOLO LA ESTRUCTURA
    // navegable con miniaturas placeholder; los fondos reales se meten despues. _dynFocus: 0 = fila
    // de pestañas, 1 = fila de miniaturas. _dynTab: 0 Games, 1 Xbox, 2 Abstract. _dynIndex: miniatura
    // seleccionada dentro de la pestaña.
    private bool _inDynamic;
    private int _dynFocus;
    private int _dynTab;
    private int _dynIndex;
    private TextBlock[] _dynTabs = null!;

    // Preview a pantalla completa del fondo ENFOCADO (aunque no este aplicado): un solo video
    // decodificando a la vez, que sigue a la miniatura seleccionada. _dynPreviewShownVideo = el video
    // que hay puesto ahora mismo; _dynPreviewTargetVideo = el que se quiere (se pone tras un respiro
    // para no crear un decoder por cada miniatura si el usuario pasa rapido). El poster (imagen fija)
    // se ve al instante mientras el video arranca. "" = sin resolver aun (fuerza la primera pasada).
    private HardwareVideoBackgroundControl? _dynPreviewVideo;
    private string? _dynPreviewTargetVideo = "";
    private DispatcherTimer? _dynPreviewTimer;

    // Numero de miniaturas por pestaña. Games ya esta poblada de verdad (31 fondos reales en DynLibrary,
    // sin placeholders). Xbox (35) y Abstract (129) siguen siendo PLACEHOLDER (conteos reales de la
    // biblioteca de videos que faltan por meter): sus miniaturas salen grises hasta que se procesen.
    // Al poblar una pestaña, poner aqui el numero real de fondos (= DynLibrary[tab].Length).
    private static readonly int[] DynTabCounts = { 31, 35, 129 };
    private const double DynThumbPitch = 274;   // ancho de miniatura (262) + hueco (12)
    private const double DynRailSelX = 131;     // x fija de la miniatura seleccionada (medida del frame)

    // Un fondo dinamico REAL: su nombre (el que se muestra) + la ruta del video y la del poster
    // (imagen fija de la miniatura), ambas relativas a Assets/Backgrounds. Se van metiendo aqui uno a
    // uno; las posiciones sin entrada siguen como placeholder gris.
    private sealed record DynBackground(string Name, string VideoRelPath, string PosterRelPath);

    // Biblioteca de fondos dinamicos reales por pestaña (0 Games, 1 Xbox, 2 Abstract), en orden. Por
    // ahora solo el primero de Games (Modern Warfare III); el resto llegara despues.
    private static readonly DynBackground[][] DynLibrary =
    {
        new[] // Games
        {
            new DynBackground(
                "Call of Duty: Modern Warfare III",
                "Games/Call of Duty Modern Warfare III.mp4",
                "Games/Call of Duty Modern Warfare III.jpg"),
            new DynBackground("Forza Horizon 6 Thematic", "Games/Forza Horizon 6 Thematic.mp4", "Games/Forza Horizon 6 Thematic.jpg"),
            new DynBackground("Forza Horizon 6", "Games/Forza Horizon 6.mp4", "Games/Forza Horizon 6.jpg"),
            new DynBackground("Avowed Key Art", "Games/Avowed Key Art.mp4", "Games/Avowed Key Art.jpg"),
            new DynBackground("Call of Duty Black Ops 6", "Games/Call of Duty Black Ops 6.mp4", "Games/Call of Duty Black Ops 6.jpg"),
            new DynBackground("Cyberpunk 2077", "Games/Cyberpunk 2077.mp4", "Games/Cyberpunk 2077.jpg"),
            new DynBackground("Diablo IV", "Games/Diablo IV.mp4", "Games/Diablo IV.jpg"),
            new DynBackground("DOOM The Dark Ages", "Games/DOOM The Dark Ages.mp4", "Games/DOOM The Dark Ages.jpg"),
            new DynBackground("Dragon Age The Veilguard (Dragon)", "Games/Dragon Age The Veilguard (Dragon).mp4", "Games/Dragon Age The Veilguard (Dragon).jpg"),
            new DynBackground("EA SPORTS FC 24", "Games/EA SPORTS FC 24.mp4", "Games/EA SPORTS FC 24.jpg"),
            new DynBackground("EA SPORTS College Football 25", "Games/EA SPORTS College Football 25.mp4", "Games/EA SPORTS College Football 25.jpg"),
            new DynBackground("F1 23", "Games/F1 23.mp4", "Games/F1 23.jpg"),
            new DynBackground("Fallout 76 Burning Springs", "Games/Fallout 76 Burning Springs.mp4", "Games/Fallout 76 Burning Springs.jpg"),
            new DynBackground("Fallout Season Two, An Amazon Original Series", "Games/Fallout Season Two, An Amazon Original Series.mp4", "Games/Fallout Season Two, An Amazon Original Series.jpg"),
            new DynBackground("Grounded Backyard Sunset", "Games/Grounded Backyard Sunset.mp4", "Games/Grounded Backyard Sunset.jpg"),
            new DynBackground("Halo Infinite - Courage", "Games/Halo Infinite - Courage.mp4", "Games/Halo Infinite - Courage.jpg"),
            new DynBackground("Halo Infinite", "Games/Halo Infinite.mp4", "Games/Halo Infinite.jpg"),
            new DynBackground("Invincible VS", "Games/Invincible VS.mp4", "Games/Invincible VS.jpg"),
            new DynBackground("Keeper", "Games/Keeper.mp4", "Games/Keeper.jpg"),
            new DynBackground("Madden NFL 24", "Games/Madden NFL 24.mp4", "Games/Madden NFL 24.jpg"),
            new DynBackground("Madden NFL 25", "Games/Madden NFL 25.mp4", "Games/Madden NFL 25.jpg"),
            new DynBackground("NHL 24 Cale Makar", "Games/NHL 24 Cale Makar.mp4", "Games/NHL 24 Cale Makar.jpg"),
            new DynBackground("Pentiment Waterfall", "Games/Pentiment Waterfall.mp4", "Games/Pentiment Waterfall.jpg"),
            new DynBackground("Sea of Thieves Reaper's Mark", "Games/Sea of Thieves Reaper's Mark.mp4", "Games/Sea of Thieves Reaper's Mark.jpg"),
            new DynBackground("Sea of Thieves Sunset", "Games/Sea of Thieves Sunset.mp4", "Games/Sea of Thieves Sunset.jpg"),
            new DynBackground("Skull and Bones", "Games/Skull and Bones.mp4", "Games/Skull and Bones.jpg"),
            new DynBackground("Split Fiction", "Games/Split Fiction.mp4", "Games/Split Fiction.jpg"),
            new DynBackground("Starfield Journey through Space", "Games/Starfield Journey through Space.mp4", "Games/Starfield Journey through Space.jpg"),
            new DynBackground("Starfield Shattered Space", "Games/Starfield Shattered Space.mp4", "Games/Starfield Shattered Space.jpg"),
            new DynBackground("The Outer Worlds 2", "Games/The Outer Worlds 2.mp4", "Games/The Outer Worlds 2.jpg"),
            new DynBackground("The Witcher 3 Wild Hunt 10th Anniversary", "Games/The Witcher 3 Wild Hunt 10th Anniversary.mp4", "Games/The Witcher 3 Wild Hunt 10th Anniversary.jpg"),
        },
        Array.Empty<DynBackground>(), // Xbox
        Array.Empty<DynBackground>(), // Abstract
    };

    // Cache del poster a RESOLUCION COMPLETA de la home (~8 MB). Se llena al APLICAR un fondo. Solo se
    // guarda UNO: el del fondo puesto ahora mismo. Al cambiar de fondo, el anterior se suelta (y libera
    // de la GPU), pero con LIBERACION DIFERIDA (ver ScheduleDispose): la foto que se acaba de quitar de
    // pantalla puede seguir "en vuelo" en la GPU un fotograma, asi que se libera un instante despues, no
    // en el acto, para no cascar.
    private readonly global::System.Collections.Generic.Dictionary<string, Avalonia.Media.Imaging.Bitmap> _dynPosterCache = new();

    // El fondo real en la posicion (pestaña, indice), o null si esa miniatura aun es placeholder.
    private static DynBackground? DynEntry(int tab, int index)
        => index >= 0 && index < DynLibrary[tab].Length ? DynLibrary[tab][index] : null;

    // Ruta completa en disco de un asset de fondos a partir de su ruta relativa. Los assets pesados
    // pueden estar junto al ejecutable (desarrollo) o en la carpeta compartida de la maquina (donde los
    // deja el instalador): decide AssetPaths, ver la explicacion de por que van aparte en AssetPaths.cs.
    private static string BackgroundFullPath(string relPath) => AssetPaths.Background(relPath);

    // El fondo por defecto (primer arranque y "Restore default background"): el PRIMERO de la
    // biblioteca. Antes era un video placeholder (dynamic-background.mp4) que el usuario borro; ahora
    // es el primer fondo real. null si la biblioteca esta vacia.
    private static DynBackground? DefaultBackground()
    {
        foreach (var tab in DynLibrary)
        {
            foreach (var e in tab)
            {
                return e; // el primero que haya
            }
        }

        return null;
    }

    // Busca un fondo de la biblioteca por su ruta de video (null si no esta).
    private static DynBackground? FindBackground(string videoRelPath)
    {
        foreach (var tab in DynLibrary)
        {
            foreach (var e in tab)
            {
                if (e.VideoRelPath == videoRelPath)
                {
                    return e;
                }
            }
        }

        return null;
    }

    // Video que debe reproducir la home: el elegido (si hay) o, por defecto, el primero de la
    // biblioteca. null si no hay ninguno (biblioteca vacia).
    private string? ResolveHomeVideoPath()
    {
        var rel = _backgroundVideoRelPath ?? DefaultBackground()?.VideoRelPath;
        return rel is null ? null : BackgroundFullPath(rel);
    }

    // El ancho del subrayado ya no es fijo: se mide del ancho real de la palabra en tiempo de
    // ejecucion (ver UpdateTabUnderline), medido sobre "linea.png".

    // Segunda accion de la barra de ayuda, por pestaña (en el frame: Games -> "See game details",
    // Xbox/Abstract -> "Change my color").
    private static readonly string[] DynHintActions =
    {
        "See game details", "Change my color", "Change my color",
    };

    public MainWindow()
    {
        InitializeComponent();

        _rows = new[]
        {
            new[] { Nav0, Nav1, Nav2, Nav3, Nav4 },
            new[] { Tile0, Tile1, Tile2, Tile3, Tile4, Tile5, Tile6, Tile7, Tile8 },
            new[] { Tile9, Tile10, Tile11, Tile12 },
        };

        _homeRings = new[]
        {
            Array.Empty<Border>(),
            new[] { Ring0, Ring1, Ring2, Ring3, Ring4, Ring5, Ring6, Ring7, Ring8 },
            new[] { Ring9, Ring10, Ring11, Ring12 },
        };

        // Centros en X calculados a partir de las mismas coordenadas fijas del XAML
        // (Canvas.Left + Width/2), en vez de leer Bounds, que no esta listo hasta el primer layout.
        // Centros en X de cada casilla, para la navegacion arriba/abajo (NearestColumn elige la
        // columna mas cercana al bajar/subir de fila). Son los centros en REPOSO (cuando bajas a la
        // fila desde arriba, las casillas aun estan repartidas, sin comprimir). Fila de juegos:
        // 110 + i*195 + 154/2. Fila de abajo: left + 400/2. Fila 0 (nav) son los circulos.
        _rowCenters = new[]
        {
            new double[] { 813.5, 885.5, 958.5, 1030.5, 1102.5 },
            new double[] { 187, 382, 577, 772, 967, 1162, 1357, 1552, 1747 },
            new double[] { 310, 748, 1186, 1624 },
        };

        _personalizationTiles = new[] { PzTile0, PzTile1, PzTile2, PzTile3, PzTile4, PzTile5 };
        _personalizationRings = new[] { PzRing0, PzRing1, PzRing2, PzRing3, PzRing4, PzRing5 };

        _colorThemeCards = new[] { CtCard0, CtCard1 };
        _colorThemeRings = new[] { CtRing0, CtRing1 };

        _myBackgroundTiles = new[] { MbTile0, MbTile1, MbTile2, MbTile3, MbTile4, MbRestore };
        _myBackgroundRings = new[] { MbRing0, MbRing1, MbRing2, MbRing3, MbRing4, MbRestoreRing };
        BuildMyBackgroundCheck();

        BuildColorSwatches();
        BuildSolidColorSwatches();
        BuildCustomImageFolders();
        _dynTabs = new[] { DynTabGames, DynTabXbox, DynTabAbstract };

        // El tema (acento) ya lo aplico App.OnFrameworkInitializationCompleted a los recursos; aqui
        // solo se sincroniza el estado local y el nombre mostrado en la tarjeta "My color".
        _currentAccentHex = AccentTheme.LoadSavedHex();
        CtColorValue.Text = AccentTheme.NameFor(_currentAccentHex);

        // Arranca con el PRIMER JUEGO seleccionado (fila de juegos, columna 0 = "Game 1"), a
        // a proposito. Xbox de fabrica arranca en la fila de navegacion ("My games &
        // apps"), pero aqui se prefiere caer directamente sobre los juegos.
        _row = 1;
        _col = 0;
        UpdateSelection();
        UserNameText.Text = global::System.Environment.UserName;

        _gamepad.ButtonPressed += OnGamepadButtonPressed;
        // B MANTENIDO: solo se usa para SALIR de la pantalla de YouTube (en el resto de la app no hace
        // nada; el toque de B ya hace de "atras" en cada pantalla).
        _gamepad.BHeld += () => CrashLog.Guard(() => { if (_inYouTube) ExitYouTube(); }, "bheld");
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        // Guard: el sondeo del mando corre aqui y de el cuelga TODA la navegacion (Poll -> ButtonPressed
        // -> Move -> handlers de cada pantalla), asi que un fallo en cualquier handler entraria por aqui.
        // Capturarlo evita que tumbe el shell (P0).
        _pollTimer.Tick += (_, _) => CrashLog.Guard(_gamepad.Poll, "poll");
        _pollTimer.Start();

        // "Respiro" antes de arrancar el video de preview del fondo enfocado: si el usuario pasa la
        // seleccion rapido por varias miniaturas, no se crea un decoder por cada una, solo cuando se
        // posa (~250 ms).
        _dynPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _dynPreviewTimer.Tick += (_, _) => CrashLog.Guard(() =>
        {
            _dynPreviewTimer!.Stop();
            if (_dynPreviewTargetVideo == null)
            {
                return; // miniatura placeholder: no hay video que reproducir
            }

            // La imagen de carga (que se vera DESENFOCADA) se queda en la MINIATURA que ya puso
            // UpdateDynPreview: con el blur no se distingue de una version mayor, asi que NO se
            // decodifica un poster grande aparte ni se guarda en RAM. Solo se cambia la fuente del video.

            // Un SOLO reproductor: se crea la primera vez y luego solo se le cambia el video (no se
            // destruye/recrea en cada cambio, que era lo que bloqueaba la pagina). Se revela cuando su
            // primer fotograma este listo (OnDynPreviewReady), no antes.
            EnsureDynPreviewControl();
            _dynPreviewVideo!.SetVideoSource(_dynPreviewTargetVideo);
        }, "dyn-preview-tick");

        KeyDown += (s, e) => CrashLog.Guard(() => OnKeyDown(s, e), "keydown");
        Opened += (_, _) => CrashLog.Guard(() => { CoverEntireMonitor(); UpdateHomeVideoState(); }, "opened");
        // El video de fondo solo se decodifica cuando la home esta realmente a la vista. Al perder el
        // foco (se abre un juego u otra ventana delante) se descarga; al recuperarlo se recarga. Ver
        // UpdateHomeVideoState.
        Activated += (_, _) => CrashLog.Guard(UpdateHomeVideoState, "activated");
        Deactivated += (_, _) => CrashLog.Guard(UpdateHomeVideoState, "deactivated");
        // CoverEntireMonitor() en el Opened de arriba solo cubre el arranque. Si despues se
        // cambia de pantalla en caliente (p.ej. la ROG Ally pasando de su pantalla integrada a
        // un monitor externo y viceversa, sin cerrar la app), Avalonia no vuelve a disparar
        // Opened - la ventana se quedaba con el tamaño/posicion de la pantalla anterior. Screens
        // dispara Changed cada vez que cambia la configuracion de pantallas conectadas
        // (resolucion, monitor añadido/quitado), asi que recalculamos ahi tambien.
        Screens.Changed += (_, _) => CrashLog.Guard(CoverEntireMonitor, "screens-changed");

        // Fondo guardado: puede ser un color solido, un video concreto (p.ej. Modern Warfare III) o el
        // video dinamico por defecto. ApplyBackground pone el poster (primer fotograma) al instante y,
        // via UpdateHomeVideoState, carga el video solo si la home esta a la vista (al arrancar aun no,
        // se carga en el Opened/Activated de arriba). Ademas coloca el check de "fondo activo" de My
        // background en la fuente correcta (Solid colors o Dynamic backgrounds).
        _backgroundSolidHex = BackgroundSettings.LoadSolidHex();
        _backgroundVideoRelPath = BackgroundSettings.LoadVideoRelPath();
        ApplyBackground();

        StartBatteryMonitor();
        StartClock();

        // Atajo de depuracion SOLO para captura/verificacion: si se define la variable de entorno
        // PLAYFRONT_DEBUG_SCREEN, la app arranca directamente en esa pantalla en vez de en la home. No
        // tiene ningun efecto en uso normal (la variable no existe). Evita tener que navegar con el
        // mando/teclado para llegar a una pantalla profunda solo para hacerle una captura. Se
        // ejecuta tras el primer layout (Post) para que la pantalla ya este medida al mostrarse.
        var debugScreen = global::System.Environment.GetEnvironmentVariable("PLAYFRONT_DEBUG_SCREEN");
        if (!string.IsNullOrEmpty(debugScreen))
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (debugScreen)
                {
                    case "mybackground":
                        EnterMyBackground();
                        break;
                    case "solidcolors":
                        EnterSolidColors();
                        break;
                    case "customimage":
                        EnterCustomImage();
                        break;
                    case "dynamic":
                        EnterDynamic();
                        break;
                    case "settings":
                        EnterSettings();
                        break;
                    case "store":
                        EnterStore();
                        break;
                    case "library":
                        EnterLibrary();
                        break;
                }
            });
        }
    }

    private void StartClock()
    {
        UpdateClock();
        var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        clockTimer.Tick += (_, _) => CrashLog.Guard(UpdateClock, "clock");
        clockTimer.Start();
    }

    private void UpdateClock()
    {
        ClockText.Text = DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    private void StartBatteryMonitor()
    {
        UpdateBatteryIcon();
        var batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        batteryTimer.Tick += (_, _) => CrashLog.Guard(UpdateBatteryIcon, "battery");
        batteryTimer.Start();
    }

    private void UpdateBatteryIcon()
    {
        _battery.Refresh();

        // Sin lectura de batería (p.ej. equipo de sobremesa sin batería): se deja el ultimo
        // estado conocido en vez de vaciar la barra.
        if (_battery.Percent is not { } percent)
        {
            return;
        }

        BatteryFill.Width = BatteryFillMaxWidth * Math.Clamp(percent / 100.0, 0.0, 1.0);

        // Con el cargador conectado se muestra el icono de carga (contorno con muesca + rayo, del
        // Battery4Charging.svg de Xbox); sin cargador, el contorno normal (Battery0). El relleno
        // verde con el % es el mismo en ambos casos.
        var charging = _battery.IsPluggedIn;
        BatteryOutline.IsVisible = !charging;
        BatteryOutlineCharging.IsVisible = charging;
        BatteryBolt.IsVisible = charging;
    }

    // WindowState="FullScreen" en Avalonia solo ocupa el area de trabajo (la pantalla
    // menos la barra de tareas de Windows), no el monitor completo. Eso dejaba franjas
    // negras a los lados porque la interfaz mantiene su proporcion 16:9 fija. Aqui se
    // fuerza el tamaño y posicion exactos del monitor, tapando la barra de tareas.
    //
    // Importante: sin Topmost. Con Topmost=true la ventana se dibuja siempre por encima
    // de cualquier otra app aunque pierda el foco (p.ej. al hacer Alt+Tab), lo que impedia
    // salir de Playfront aunque el cambio de ventana funcionase "por dentro". Sin Topmost, en
    // cuanto otra ventana pasa a primer plano queda por delante con normalidad.
    private void CoverEntireMonitor()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        WindowState = WindowState.Normal;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
    }

    // Ruta del video que se esta reproduciendo ahora mismo como fondo (para no recrear el control si
    // no cambia).
    private string? _currentVideoPath;

    // Pone (o cambia) el video de fondo de la home. Si es OTRO video y ya hay reproductor, solo le
    // cambia la fuente (SetVideoSource) -sin destruir/recrear- para que aplicar un fondo distinto sea
    // INSTANTANEO, sin el cuelgue del desmontaje del decodificador. Solo se quita el control cuando de
    // verdad hay que descargar el video (fondo solido, o Playfront pierde el primer plano: fullPath null o
    // inexistente).
    private void SetVideoBackground(string? fullPath)
    {
        if (_currentVideoPath == fullPath && _videoBackground != null)
        {
            return;
        }

        if (fullPath == null || !File.Exists(fullPath))
        {
            if (_videoBackground != null)
            {
                BackgroundHost.Children.Remove(_videoBackground);
                _videoBackground = null;
            }
            _currentVideoPath = null;
            return;
        }

        if (_videoBackground == null)
        {
            _videoBackground = new HardwareVideoBackgroundControl(fullPath) { Width = 1920, Height = 1080 };
            BackgroundHost.Children.Add(_videoBackground);
        }
        else
        {
            _videoBackground.SetVideoSource(fullPath);
        }
        _currentVideoPath = fullPath;
    }

    private void OnGamepadButtonPressed(GamepadButton button) => Move(button);

    private bool _steamInstalling;

    // TEMPORAL: la casilla "Game 1" hace de boton de instalar Steam. Le pide al ayudante (SYSTEM) que lo
    // instale (descarga + verifica firma + instala sin UAC); si ya esta instalado, lo dice. Va
    // actualizando la etiqueta con el estado. Se movera a su sitio definitivo al montar la biblioteca.
    private async global::System.Threading.Tasks.Task InstallSteamFromButtonAsync()
    {
        if (_steamInstalling)
        {
            return;
        }
        _steamInstalling = true;
        SteamButtonLabel.Text = "Installing…";
        try
        {
            var response = await HelperClient.SendAsync("install-steam", TimeSpan.FromSeconds(5));
            SteamButtonLabel.Text = response.Ok ? "Steam ready" : "Failed";
        }
        catch (global::System.Exception)
        {
            SteamButtonLabel.Text = "Helper off";
        }
        finally
        {
            _steamInstalling = false;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                Move(GamepadButton.Up);
                break;
            case Key.Down:
                Move(GamepadButton.Down);
                break;
            case Key.Left:
                Move(GamepadButton.Left);
                break;
            case Key.Right:
                Move(GamepadButton.Right);
                break;
            case Key.Enter:
                Move(GamepadButton.A);
                break;
            // "Atras" con teclado: Retroceso y ESCAPE hacen lo mismo que la B del mando.
            // Escape se anadio el 2026-07-26 porque es lo primero que prueba cualquiera para
            // salir de una pantalla, y hasta entonces no hacia NADA: quien se quedara sin mando
            // a mano no tenia forma de volver atras. Ojo, es "atras", NO "cerrar la app": el
            // Escape que cerraba Playfront se quito a proposito y no vuelve.
            case Key.Back:
            case Key.Escape:
                Move(GamepadButton.B);
                break;
            // Bumpers LB/RB en teclado (para probar sin mando): Q y E.
            case Key.Q:
                Move(GamepadButton.LB);
                break;
            case Key.E:
                Move(GamepadButton.RB);
                break;
        }
    }

    // El motor de actualizaciones de la app, uno solo y compartido: lo usa la pantalla
    // System -> Updates. Vive aqui y no en la pantalla porque el estado tiene que sobrevivir a
    // cerrarla: si descargas una actualizacion y sales, al volver sigue lista para aplicarse en vez
    // de empezar de cero.
    private readonly UpdateService _updates = new();

    private void Move(GamepadButton button)
    {
        // El orden aqui decide que pantalla recibe el mando: se comprueba de la mas encima a la mas
        // debajo. "My color & theme" esta encima de Personalization, que esta encima de Ajustes;
        // todas siguen montadas por detras (con su _inX en true) para que al cerrar con B cada una
        // aparezca tal y como estaba.
        if (_inColorPicker)
        {
            MoveColorPicker(button);
            return;
        }

        if (_inColorTheme)
        {
            MoveColorTheme(button);
            return;
        }

        // "Solid colors" cuelga de "My background", asi que va por encima de ella.
        if (_inSolidColors)
        {
            MoveSolidColors(button);
            return;
        }

        // "Custom image" tambien cuelga de "My background" (pantalla solo visual).
        if (_inCustomImage)
        {
            MoveCustomImage(button);
            return;
        }

        // "Dynamic backgrounds" tambien cuelga de "My background".
        if (_inDynamic)
        {
            MoveDynamic(button);
            return;
        }

        // "My background" tambien cuelga de Personalization (hermana de "My color & theme"), asi que
        // se comprueba antes que ella. Nunca estan abiertas las dos a la vez.
        if (_inMyBackground)
        {
            MoveMyBackground(button);
            return;
        }

        if (_inPersonalization)
        {
            MovePersonalization(button);
            return;
        }

        // "System Updates" cuelga de Ajustes y esta POR ENCIMA, asi que se comprueba antes.
        if (_inUpdates)
        {
            _updatesView?.Move(button);
            return;
        }

        if (_inSettings)
        {
            _settingsView?.Move(button);
            return;
        }

        // La Tienda es una pantalla completa (como Ajustes): mientras esta abierta, el mando la
        // navega a ella. Su vista decide cuando salir (B en el nivel superior -> ExitStore).
        // La pagina de categoria esta POR ENCIMA de la Tienda: si esta abierta, el mando es suyo.
        // YouTube (app web a pantalla completa) esta POR ENCIMA de todo: si esta abierto, el mando es suyo.
        if (_inYouTube)
        {
            MoveYouTube(button);
            return;
        }

        // La ficha de producto esta POR ENCIMA de la pagina de categoria, asi que se mira antes.
        if (_inApp)
        {
            _appView?.Move(button);
            return;
        }

        if (_inCategory)
        {
            _categoryView?.Move(button);
            return;
        }

        if (_inStore)
        {
            _storeView?.Move(button);
            return;
        }

        // La Biblioteca es una pantalla completa (como Ajustes/Tienda): mientras esta abierta, el
        // mando la navega a ella. Por ahora solo sale con B.
        if (_inLibrary)
        {
            _libraryView?.Move(button);
            return;
        }

        switch (button)
        {
            // El icono "My games & apps" es el PRIMERO (columna 0) de la fila de navegacion (fila 0).
            case GamepadButton.A when _row == 0 && _col == 0:
                EnterLibrary();
                return;
            // El icono de Ajustes es el ultimo (columna 4) de la fila de navegacion (fila 0).
            case GamepadButton.A when _row == 0 && _col == 4:
                EnterSettings();
                return;
            // El icono de la Tienda es la columna 1 (bolsa) de la fila de navegacion (fila 0).
            case GamepadButton.A when _row == 0 && _col == 1:
                EnterStore();
                return;
            // TEMPORAL: la casilla "Game 1" (fila 1, col 0) hace de boton "Install Steam" por ahora.
            case GamepadButton.A when _row == 1 && _col == 0:
                _ = InstallSteamFromButtonAsync();
                return;
            case GamepadButton.Left when _col > 0:
                _col--;
                break;
            case GamepadButton.Right when _col < _rows[_row].Length - 1:
                _col++;
                break;
            case GamepadButton.Up when _row > 0:
                _row--;
                _col = NearestColumn(_row, _rowCenters[_row + 1][_col]);
                break;
            case GamepadButton.Down when _row < _rows.Length - 1:
                _row++;
                _col = NearestColumn(_row, _rowCenters[_row - 1][_col]);
                break;
            default:
                return;
        }

        UpdateSelection();
    }

    private async void EnterSettings()
    {
        if (_settingsTransitioning)
        {
            return;
        }

        // _inSettings se marca ya aqui (antes de cualquier "await") para que si el usuario
        // sigue tocando el mando mientras se ve el engranaje de carga, el mando ya navegue
        // dentro de Ajustes y no vuelva a disparar EnterSettings por segunda vez.
        _inSettings = true;
        _settingsTransitioning = true;
        UpdateHomeVideoState(); // home tapada por Ajustes: pausa el video de fondo (no se ve)

        // P0: envuelto para que un fallo a mitad de transicion NO deje el velo negro colgado ni la
        // bandera _settingsTransitioning atascada (eso bloquearia Ajustes para siempre).
        try
        {
            await RunEnterSettingsTransition();
        }
        catch (Exception e)
        {
            CrashLog.Log("enter-settings", e);
        }
        finally
        {
            _settingsTransitioning = false;
            SettingsLoadingScreen.Opacity = 0;
            SettingsLoadingScreen.IsVisible = false;
            // Si fallo ANTES de montar la vista, no dejar al usuario "dentro de Ajustes" sin pantalla
            // (el input iria a la nada y B no saldria): volver limpiamente a la home.
            if (_settingsView == null && _inSettings)
            {
                _inSettings = false;
                UpdateHomeVideoState();
                UpdateSelection();
            }
        }
    }

    // Secuencia de entrada a Ajustes (velo de carga + montaje de la vista bajo demanda). Separada para
    // que EnterSettings pueda envolverla en try/finally (ver alli).
    private async Task RunEnterSettingsTransition()
    {
        var stopwatch = Stopwatch.StartNew();

        SettingsLoadingScreen.IsVisible = true;
        // Deja que Avalonia pinte un frame con opacidad 0 antes de subirla a 1 - si las dos
        // asignaciones cayeran en el mismo frame, el fundido de entrada no se veria (pasaria
        // de invisible a opaco de golpe en vez de animarse).
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        SettingsLoadingScreen.Opacity = 1;

        // Importante: no se prepara Ajustes por detras hasta que el fundido de entrada haya
        // terminado del todo (velo 100% opaco). Si se hiciera antes, Ajustes se veria "a
        // traves" del velo mientras este todavia es semitransparente, en vez de aparecer
        // limpiamente detras de un negro solido.
        await Task.Delay(SettingsLoadingFadeDuration);

        // Monta la vista de Ajustes AQUI (bajo el velo negro a plena opacidad, asi no se ve la
        // construccion) y se libera al salir (ExitSettings): la Home es lo unico residente al arrancar.
        // La vista arranca en su estado por defecto (categoria "General") y se dibuja en su constructor.
        _settingsView = new SettingsView();
        _settingsView.PersonalizationRequested += EnterPersonalization;
        _settingsView.UpdatesRequested += EnterUpdates;
        _settingsView.ExitRequested += ExitSettings;
        SettingsHost.Children.Add(_settingsView);

        // Espera a que Avalonia complete una pasada de layout/render de la pantalla de
        // Ajustes recien mostrada (relevante si en ese momento hay un juego pesado ocupando
        // la CPU/GPU y tarda en llegar a dibujarse), y ademas se asegura de los 300ms minimos
        // de arriba - lo que tarde mas de los dos.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        var remaining = MinSettingsLoadingMilliseconds - stopwatch.ElapsedMilliseconds;
        if (remaining > 0)
        {
            await Task.Delay((int)remaining);
        }

        SettingsLoadingScreen.Opacity = 0;
        await Task.Delay(SettingsLoadingFadeDuration);
        SettingsLoadingScreen.IsVisible = false;
    }

    // Salida de Ajustes: vuelta directa al Home, sin velo de carga. El velo del engranaje solo
    // se usa al entrar (donde hace falta cubrir la preparacion de la pantalla de Ajustes); el
    // Home ya esta montado por detras, asi que salir no tiene nada que cubrir.
    private void ExitSettings()
    {
        if (_settingsTransitioning)
        {
            return;
        }

        _inSettings = false;

        // Red de seguridad: si por lo que sea se sale de Ajustes con "System Updates" todavia
        // montada, se cierra con el. Sin esto quedaria una pantalla huerfana encima de la home,
        // sin nadie que la navegue ni la pueda cerrar.
        if (_inUpdates)
        {
            ExitUpdates();
        }

        if (_settingsView != null)
        {
            _settingsView.PersonalizationRequested -= EnterPersonalization;
            _settingsView.UpdatesRequested -= EnterUpdates;
            _settingsView.ExitRequested -= ExitSettings;
            SettingsHost.Children.Remove(_settingsView);
            _settingsView = null; // libera la vista: el recolector de basura recupera su memoria
        }
        UpdateHomeVideoState(); // de vuelta en la home: reanuda el video de fondo
        UpdateSelection();
    }

    // Entrar/salir de la TIENDA. Por ahora sin velo de carga (FASE 1: la vista es ligera, solo el
    // fondo). Cuando el contenido crezca (imagenes reales) se añadira un velo para tapar la
    // construccion, como en EnterSettings. La vista se monta bajo demanda y se libera al salir.
    private void EnterStore()
    {
        if (_inStore)
        {
            return;
        }

        _inStore = true;
        _storeView = new StoreView();
        _storeView.ExitRequested += ExitStore;
        _storeView.CategoryRequested += EnterCategory;
        StoreHost.Children.Add(_storeView);
        UpdateHomeVideoState(); // home tapada por la Tienda: pausa el video de fondo (no se ve)
    }

    // ===== Pagina de categoria de la Tienda (Apps > Music apps) =====
    // Se monta encima de la Tienda y OCULTA su host mientras esta puesta: la pagina es opaca y
    // pintar la Tienda por debajo seria trabajo tirado. La Tienda se queda montada (no se libera)
    // para volver a ella con B sin reconstruirla ni perder donde estaba.
    private bool _inCategory;
    private StoreCategoryView? _categoryView;

    private void EnterCategory(string category)
    {
        if (_inCategory)
        {
            return;
        }

        _inCategory = true;
        _categoryView = new StoreCategoryView();
        _categoryView.ExitRequested += ExitCategory;
        _categoryView.AppRequested += EnterApp;
        CategoryHost.Children.Add(_categoryView);
        StoreHost.IsVisible = false;
    }

    private void ExitCategory()
    {
        _inCategory = false;
        if (_categoryView != null)
        {
            _categoryView.ExitRequested -= ExitCategory;
            _categoryView.AppRequested -= EnterApp;
            CategoryHost.Children.Remove(_categoryView);
            _categoryView = null; // libera la vista y su arte
        }

        StoreHost.IsVisible = true;
    }

    // ===== Ficha de producto de una app (Music apps > YouTube) =====
    // Un piso mas arriba que la pagina de categoria, con el mismo patron: se monta al entrar, se
    // libera al salir, y oculta la pantalla de debajo mientras esta puesta.
    private bool _inApp;
    private StoreAppView? _appView;

    private void EnterApp(string art)
    {
        if (_inApp)
        {
            return;
        }

        _inApp = true;
        _appView = new StoreAppView(art);
        _appView.ExitRequested += ExitApp;
        _appView.ActionInvoked += OnAppActionInvoked;
        AppHost.Children.Add(_appView);
        CategoryHost.IsVisible = false;
    }

    // Boton principal (INSTALL/PLAY) de una ficha de app. De momento solo YouTube tiene comportamiento:
    // lanza su app web. La persistencia del estado "instalada" (registro + boton PLAY + tile) llega en el
    // siguiente paso; ahora el boton ya ABRE YouTube, que es lo que se quiere probar.
    private void OnAppActionInvoked(string art)
    {
        if (art == "youtube.png")
        {
            EnterYouTube();
        }
    }

    private void ExitApp()
    {
        _inApp = false;
        if (_appView != null)
        {
            _appView.ExitRequested -= ExitApp;
            _appView.ActionInvoked -= OnAppActionInvoked;
            AppHost.Children.Remove(_appView);
            _appView = null; // libera la vista y su arte
        }

        CategoryHost.IsVisible = true;
    }

    // ===== App web de YouTube (interfaz de TV, dentro de un WebView2) =====
    // Se monta a pantalla completa POR ENCIMA de todo (YouTubeHost esta fuera del Viewbox escalado). El
    // navegador es una ventana nativa que se dibuja siempre encima, asi que aqui no hay UI de Playfront
    // superpuesta: es YouTube a pantalla completa. Se libera del todo al salir (se van los procesos del
    // navegador). Ver src/Playfront.App/Web/WebViewHost.cs.
    private bool _inYouTube;
    private Web.WebViewHost? _youTube;

    // Carpeta de perfil del navegador para YouTube: aqui viven cookies y la sesion (login persistente).
    private static string YouTubeProfileFolder => AppData.File("YouTube");

    private void EnterYouTube()
    {
        if (_inYouTube)
        {
            return;
        }

        _inYouTube = true;
        _youTube = new Web.WebViewHost("https://www.youtube.com/tv", YouTubeProfileFolder);
        _youTube.InitFailed += OnYouTubeInitFailed;
        YouTubeHost.Children.Add(_youTube);
        YouTubeHost.IsVisible = true;

        // Aparcar el resto: soltar el video de fondo de la home (no correr dos tuberias de video a la vez)
        // y activar el auto-repeat del mando para pasar los rails largos deprisa.
        UpdateHomeVideoState();
        _gamepad.RepeatEnabled = true;
    }

    private void ExitYouTube()
    {
        _inYouTube = false;
        if (_youTube != null)
        {
            _youTube.InitFailed -= OnYouTubeInitFailed;
            YouTubeHost.Children.Remove(_youTube); // dispara DestroyNativeControlCore -> cierra el navegador
            _youTube = null;
        }

        YouTubeHost.IsVisible = false;
        _gamepad.RepeatEnabled = false;
        UpdateHomeVideoState(); // reanuda el fondo de la home
    }

    private void OnYouTubeInitFailed(string message)
    {
        // Lo mas comun seria que faltara el runtime de WebView2 (aqui ya esta instalado). De momento solo
        // se registra y se sale de la pantalla; el instalador del runtime via el ayudante llega despues.
        CrashLog.Log($"WebView2 no inicializo: {message}", null);
        ExitYouTube();
    }

    // Traduce el mando a las teclas que entiende la interfaz Leanback de YouTube y las inyecta en la
    // pagina (keyCodes JS estandar). Mapa CALCADO del de la app de YouTube de Xbox (fuentes: YouTube
    // Help para Xbox Series X|S y Xbox One):
    //   - Cruceta/stick = navegar; DENTRO del video, izq/der = retroceder/avanzar (lo hace la propia
    //     Leanback con las flechas, no hace falta mapa aparte).
    //   - A = seleccionar (Enter).
    //   - B = atras. En Xbox no hay "salir de la app" con el mando (se usa el boton Xbox); aqui, salir a
    //     Playfront se hace MANTENIENDO B (evento BHeld -> ExitYouTube, ver constructor).
    //   - Y = buscar.
    // Añadido nuestro (Xbox lo hace por la barra del reproductor, mas incomodo): X y Start = play/pausa.
    // keyCodes marcados TENTATIVO no estan confirmados contra Leanback; se afinan probando en la Ally.
    private void MoveYouTube(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.Up: _youTube?.SendKey(38); break;
            case GamepadButton.Down: _youTube?.SendKey(40); break;
            case GamepadButton.Left: _youTube?.SendKey(37); break;
            case GamepadButton.Right: _youTube?.SendKey(39); break;
            case GamepadButton.A: _youTube?.SendKey(13); break;    // Enter = seleccionar
            case GamepadButton.B: _youTube?.SendKey(27); break;    // Escape = atras (dentro de YouTube)
            // Buscar con TECLADO en pantalla. keyCode 170 (el "asterisco" que la interfaz Leanback usa
            // para su busqueda de teclado): confirmado en VacuumTube, el wrapper de youtube.com/tv. El
            // 191 ("/") que se probo antes abria la busqueda por VOZ, que no es lo que se quiere.
            case GamepadButton.Y: _youTube?.SendKey(170); break;
            case GamepadButton.X: _youTube?.SendKey(32); break;    // Espacio = play/pausa
            case GamepadButton.Start: _youTube?.SendKey(32); break; // Espacio = play/pausa (redundante, comodo)
            case GamepadButton.LT: _youTube?.SendKey(113); break;  // F2 = retroceder en el video
            case GamepadButton.RT: _youTube?.SendKey(114); break;  // F3 = avanzar en el video
        }
    }

    private void ExitStore()
    {
        _inStore = false;
        if (_storeView != null)
        {
            _storeView.ExitRequested -= ExitStore;
            _storeView.CategoryRequested -= EnterCategory;
            StoreHost.Children.Remove(_storeView);
            _storeView = null; // libera la vista: el recolector de basura recupera su memoria
        }
        UpdateHomeVideoState(); // de vuelta en la home: reanuda el video de fondo
        UpdateSelection();
    }

    // Entrar/salir de la BIBLIOTECA ("My games & apps"). Mismo patron que la Tienda: la vista se monta
    // bajo demanda y se libera al salir. Sin velo de carga por ahora (la vista es ligera).
    private void EnterLibrary()
    {
        if (_inLibrary)
        {
            return;
        }

        _inLibrary = true;
        _libraryView = new LibraryView();
        _libraryView.ExitRequested += ExitLibrary;
        LibraryHost.Children.Add(_libraryView);
        UpdateHomeVideoState(); // home tapada por la Biblioteca: pausa el video de fondo (no se ve)
    }

    private void ExitLibrary()
    {
        _inLibrary = false;
        if (_libraryView != null)
        {
            _libraryView.ExitRequested -= ExitLibrary;
            LibraryHost.Children.Remove(_libraryView);
            _libraryView = null; // libera la vista: el recolector de basura recupera su memoria
        }
        UpdateHomeVideoState(); // de vuelta en la home: reanuda el video de fondo
        UpdateSelection();
    }

    // Entrar/salir de Personalization no lleva velo de carga (a diferencia de abrir Ajustes desde
    // la home): la pantalla ya esta montada por detras, no hay nada pesado que preparar y por
    // tanto nada que cubrir.
    // "System Updates" cuelga de la tarjeta "Updates" de Ajustes > System. Se monta bajo demanda y
    // se libera al salir, igual que la propia vista de Ajustes: la Home es lo unico residente.
    // Mientras esta puesta, SettingsHost se oculta - la pantalla es opaca y pintar Ajustes por
    // debajo seria trabajo tirado.
    private void EnterUpdates()
    {
        if (_updatesView != null)
        {
            return;
        }

        _inUpdates = true;
        // Se le pasa el MISMO servicio que usa el resto de la app: si hubiera uno por pantalla, cada
        // visita perderia la descarga en curso y volveria a preguntar por la red desde cero.
        _updatesView = new SystemUpdatesView(_updates);
        _updatesView.ExitRequested += ExitUpdates;
        UpdatesHost.Children.Add(_updatesView);
        SettingsHost.IsVisible = false;
    }

    private void ExitUpdates()
    {
        _inUpdates = false;
        SettingsHost.IsVisible = true;

        if (_updatesView != null)
        {
            _updatesView.ExitRequested -= ExitUpdates;
            UpdatesHost.Children.Remove(_updatesView);
            _updatesView = null;
        }
    }

    private void EnterPersonalization()
    {
        _inPersonalization = true;
        _personalizationIndex = 0;
        PersonalizationScreen.IsVisible = true;
        UpdateHomeVideoState(); // home tapada por Personalization (y sus subpantallas): pausa el video
        UpdatePersonalizationSelection();
    }

    private void ExitPersonalization()
    {
        _inPersonalization = false;
        PersonalizationScreen.IsVisible = false;
        UpdateHomeVideoState(); // de vuelta en la home: reanuda el video de fondo
    }

    private void MovePersonalization(GamepadButton button)
    {
        switch (button)
        {
            // "My color & theme" es la tarjeta indice 3 (ver PzTile3 en el XAML).
            case GamepadButton.A when _personalizationIndex == 3:
                EnterColorTheme();
                return;
            // "My background" es la tarjeta indice 4 (ver PzTile4).
            case GamepadButton.A when _personalizationIndex == 4:
                EnterMyBackground();
                return;
            case GamepadButton.B:
                ExitPersonalization();
                return;
            case GamepadButton.Up when _personalizationIndex > 0:
                _personalizationIndex--;
                break;
            case GamepadButton.Down when _personalizationIndex < _personalizationTiles.Length - 1:
                _personalizationIndex++;
                break;
            default:
                return;
        }

        UpdatePersonalizationSelection();
    }

    // Entrar/salir de "My color & theme": la pantalla ya esta montada, no hay velo de carga (igual
    // que Personalization). Al cerrar con B vuelve a Personalization tal y como estaba.
    private void EnterColorTheme()
    {
        _inColorTheme = true;
        _colorThemeIndex = 0;
        ColorThemeScreen.IsVisible = true;
        UpdateColorThemeSelection();
    }

    private void ExitColorTheme()
    {
        _inColorTheme = false;
        ColorThemeScreen.IsVisible = false;
    }

    private void MoveColorTheme(GamepadButton button)
    {
        switch (button)
        {
            // "My color" (tarjeta 0) abre el selector de color.
            case GamepadButton.A when _colorThemeIndex == 0:
                EnterColorPicker();
                return;
            case GamepadButton.B:
                ExitColorTheme();
                return;
            case GamepadButton.Up when _colorThemeIndex > 0:
                _colorThemeIndex--;
                break;
            case GamepadButton.Down when _colorThemeIndex < _colorThemeCards.Length - 1:
                _colorThemeIndex++;
                break;
            default:
                return;
        }

        UpdateColorThemeSelection();
    }

    // Genera los 14 recuadros de color y sus anillos dentro de SwatchHost. Rejilla 7x2: 243x207 cada
    // uno, columnas cada 258 desde x=99, fila 1 en y=313 y fila 2 en y=536 (medido de "2.png" x0.75...
    // realmente el factor de la captura, 1.3502). Se llama una vez desde el constructor.
    private void BuildColorSwatches()
    {
        for (var i = 0; i < ColorSwatchHexes.Length; i++)
        {
            var col = i % 7;
            var row = i / 7;
            var x = 99 + col * 258;
            var y = row == 0 ? 313 : 536;

            var swatch = new Border
            {
                Width = 243,
                Height = 207,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.Parse(ColorSwatchHexes[i])),
            };
            Canvas.SetLeft(swatch, x);
            Canvas.SetTop(swatch, y);
            SwatchHost.Children.Add(swatch);
        }

        // Los anillos se añaden DESPUES de todos los recuadros para que queden por encima (su halo
        // verde no queda tapado por el recuadro vecino). Mismo anillo verde que el resto de Ajustes.
        for (var i = 0; i < ColorSwatchHexes.Length; i++)
        {
            var col = i % 7;
            var row = i / 7;
            var x = 99 + col * 258;
            var y = row == 0 ? 313 : 536;

            var ring = new Border { Width = 243 + 16, Height = 207 + 16 };
            ring.Classes.Add("selectionRing");
            Canvas.SetLeft(ring, x - 8);
            Canvas.SetTop(ring, y - 8);
            SwatchHost.Children.Add(ring);
            _colorSwatchRings[i] = ring;
        }

        // Marca de "color aplicado": triangulo blanco (#EBEBEB) en la esquina superior derecha
        // (catetos ~68) + un check oscuro fino encima. Medido de "3.png". Se añade al final para que
        // quede por encima de recuadros y anillos; se coloca/oculta en RefreshAppliedColorUi.
        _appliedCheck = new Canvas { Width = 68, Height = 68, IsVisible = false, ZIndex = 5 };
        _appliedCheck.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.Parse("#EBEBEB")),
            Data = Geometry.Parse("M 0,0 L 68,0 L 68,68 Z"),
        });
        var tick = new Avalonia.Controls.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.Parse("#1A1A1A")),
            StrokeThickness = 3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M 0,9 L 7,16 L 22,0"),
        };
        Canvas.SetLeft(tick, 30);
        Canvas.SetTop(tick, 14);
        _appliedCheck.Children.Add(tick);
        SwatchHost.Children.Add(_appliedCheck);
    }

    // Entrar/salir del selector de color. Arranca con el foco en el primer recuadro. Al cerrar (B u
    // OK) vuelve a "My color & theme". NOTA: por ahora solo es visual/navegable - elegir un color y
    // dar OK todavia NO cambia el acento real de la app (eso necesita pasar los recursos a dinamicos;
    // pendiente).
    private void EnterColorPicker()
    {
        _inColorPicker = true;
        // Arranca el foco sobre el color actualmente aplicado (si es uno de los 14); si no, el
        // primero.
        _colorPickerIndex = Math.Max(0, PaletteIndexOf(_currentAccentHex));
        ColorPickerUserName.Text = global::System.Environment.UserName;
        RefreshAppliedCheck();
        ColorPickerScreen.IsVisible = true;
        UpdateColorPickerSelection();
    }

    private void ExitColorPicker()
    {
        _inColorPicker = false;
        ColorPickerScreen.IsVisible = false;
    }

    private void MoveColorPicker(GamepadButton button)
    {
        var i = _colorPickerIndex; // 0..13 = recuadros, 14 = OK
        switch (button)
        {
            case GamepadButton.B:
                ExitColorPicker();
                return;
            case GamepadButton.A when i == 14: // OK -> cerrar
                ExitColorPicker();
                return;
            case GamepadButton.A when i < 14: // elegir este color: se aplica a TODA la app en caliente
                ApplyAccent(AccentTheme.Palette[i].Hex);
                return; // se queda en el selector para poder ver el cambio y probar otros
            case GamepadButton.Left when i < 14 && i % 7 > 0:
                i--;
                break;
            case GamepadButton.Right when i < 14 && i % 7 < 6:
                i++;
                break;
            case GamepadButton.Up when i >= 7 && i < 14:
                i -= 7;
                break;
            case GamepadButton.Up when i == 14: // OK -> fila 2, primera columna
                i = 7;
                break;
            case GamepadButton.Down when i < 7:
                i += 7;
                break;
            case GamepadButton.Down when i >= 7 && i < 14: // fila 2 -> OK
                i = 14;
                break;
            default:
                return;
        }

        _colorPickerIndex = i;
        UpdateColorPickerSelection();
    }

    private void UpdateColorPickerSelection()
    {
        for (var i = 0; i < _colorSwatchRings.Length; i++)
        {
            _colorSwatchRings[i].Classes.Set("selected", i == _colorPickerIndex);
        }

        var okSelected = _colorPickerIndex == 14;
        OkRing.Classes.Set("selected", okSelected);
        OkButton.Classes.Set("selected", okSelected);

        UpdateColorPickerTitle();
    }

    // Aplica un color de acento a TODA la app EN CALIENTE (via recursos dinamicos: bordes/anillos de
    // seleccion, sus halos, resaltes de Ajustes, el circulo de "My color"...) y lo persiste para la
    // proxima sesion. La bateria NO sigue el tema (decision del usuario: verde fijo).
    private void ApplyAccent(string hex)
    {
        AccentTheme.Apply(Application.Current!, Color.Parse(hex));
        AccentTheme.Save(hex);
        _currentAccentHex = hex;
        CtColorValue.Text = AccentTheme.NameFor(hex);
        RefreshAppliedCheck();
    }

    // Indice (0..13) del recuadro con ese color, o -1 si no es ninguno de los 14 (p.ej. el verde por
    // defecto #439941, que no esta en la rejilla).
    private static int PaletteIndexOf(string hex)
    {
        for (var i = 0; i < AccentTheme.Palette.Length; i++)
        {
            if (string.Equals(AccentTheme.Palette[i].Hex, hex, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Coloca la marca de "aplicado" (check) sobre el recuadro del acento actual. Si el acento no es
    // ninguno de los 14, oculta el check. (El titulo NO se toca aqui: sigue al color ENFOCADO, no al
    // aplicado - ver UpdateColorPickerTitle.)
    private void RefreshAppliedCheck()
    {
        if (_appliedCheck is null)
        {
            return;
        }

        var idx = PaletteIndexOf(_currentAccentHex);
        if (idx >= 0)
        {
            var col = idx % 7;
            var row = idx / 7;
            var x = 99 + col * 258;
            var y = row == 0 ? 313 : 536;
            Canvas.SetLeft(_appliedCheck, x + 243 - 68);
            Canvas.SetTop(_appliedCheck, y);
            _appliedCheck.IsVisible = true;
        }
        else
        {
            _appliedCheck.IsVisible = false;
        }
    }

    // El titulo muestra el color que se esta ENFOCANDO (el recuadro bajo el cursor), y va cambiando
    // al moverse por la rejilla: "My color - <nombre>". Con el foco en el boton OK, muestra el color
    // aplicado (o solo "My color" si el aplicado no es de la paleta).
    private void UpdateColorPickerTitle()
    {
        string? name;
        if (_colorPickerIndex < 14)
        {
            name = AccentTheme.Palette[_colorPickerIndex].Name;
        }
        else
        {
            var idx = PaletteIndexOf(_currentAccentHex);
            name = idx >= 0 ? AccentTheme.Palette[idx].Name : null;
        }

        ColorPickerTitle.Text = name is null ? "My color" : "My color - " + name;
    }

    // Descripcion que se muestra a la derecha segun la tarjeta seleccionada. La de "My color" es la
    // de la referencia (adaptada a Playfront); la de "System theme" es propia (la referencia solo
    // mostraba la primera).
    private static readonly string[] ColorThemeDescriptions =
    {
        "Choose an accent color for Playfront.",
        "Choose a light or dark system theme.",
    };

    private void UpdateColorThemeSelection()
    {
        for (var i = 0; i < _colorThemeCards.Length; i++)
        {
            var isSelected = i == _colorThemeIndex;
            _colorThemeCards[i].Classes.Set("selected", isSelected);
            _colorThemeRings[i].Classes.Set("selected", isSelected);
        }

        CtDescription.Text = ColorThemeDescriptions[_colorThemeIndex];
    }

    private void UpdatePersonalizationSelection()
    {
        for (var i = 0; i < _personalizationTiles.Length; i++)
        {
            var isSelected = i == _personalizationIndex;
            _personalizationTiles[i].Classes.Set("selected", isSelected);
            _personalizationRings[i].Classes.Set("selected", isSelected);
        }
    }

    // Entrar/salir de "My background": la pantalla ya esta montada, no hay velo de carga (igual que
    // Personalization). Arranca con el foco en "Solid colors" (indice 0, como en la referencia). Al
    // cerrar con B vuelve a Personalization tal y como estaba.
    private void EnterMyBackground()
    {
        _inMyBackground = true;
        _myBackgroundIndex = 0;
        _myBackgroundLeftReturn = 0;
        MyBackgroundScreen.IsVisible = true;
        UpdateMyBackgroundSelection();
    }

    private void ExitMyBackground()
    {
        _inMyBackground = false;
        MyBackgroundScreen.IsVisible = false;
    }

    private void MoveMyBackground(GamepadButton button)
    {
        var i = _myBackgroundIndex; // 0..4 = columna izquierda, 5 = boton "Restore default background"
        switch (button)
        {
            case GamepadButton.B:
                ExitMyBackground();
                return;
            // "Solid colors" (casilla 0) abre su selector.
            case GamepadButton.A when i == 0:
                EnterSolidColors();
                return;
            // "Custom image" (casilla 2) abre su pantalla (solo visual por ahora).
            case GamepadButton.A when i == 2:
                EnterCustomImage();
                return;
            // "Dynamic backgrounds" (casilla 4) abre su pantalla (estructura navegable, sin fondos aun).
            case GamepadButton.A when i == 4:
                EnterDynamic();
                return;
            // "Restore default background" (boton, indice 5): vuelve al fondo por defecto (el video
            // dinamico) y mueve alli el check.
            case GamepadButton.A when i == 5:
                RestoreDefaultBackground();
                return;
            // A sobre las demas fuentes (Achievement art / Custom image / Screenshots): sus
            // sub-pantallas se construiran en los pasos siguientes; por ahora no hacen nada.
            case GamepadButton.A:
                return;
            case GamepadButton.Up when i is > 0 and < 5:
                i--;
                break;
            case GamepadButton.Down when i < 4:
                i++;
                break;
            // Derecha desde cualquier casilla de la izquierda salta al boton "Restore"; se recuerda
            // desde cual para que Izquierda vuelva a ella.
            case GamepadButton.Right when i < 5:
                _myBackgroundLeftReturn = i;
                i = 5;
                break;
            case GamepadButton.Left when i == 5:
                i = _myBackgroundLeftReturn;
                break;
            default:
                return;
        }

        _myBackgroundIndex = i;
        UpdateMyBackgroundSelection();
    }

    private void UpdateMyBackgroundSelection()
    {
        for (var i = 0; i < _myBackgroundTiles.Length; i++)
        {
            var isSelected = i == _myBackgroundIndex;
            _myBackgroundTiles[i].Classes.Set("selected", isSelected);
            _myBackgroundRings[i].Classes.Set("selected", isSelected);
        }
    }

    // Triangulo blanco + check ("fondo activo") en la esquina superior derecha de la fuente de fondo
    // activa. Cateto ~53 (medido ~71px en el frame x0.75). Mismo estilo que la marca de "color
    // aplicado" del selector de color: triangulo #EBEBEB con la esquina recta arriba-derecha + un
    // tick oscuro fino centrado en su masa. Se añade a MyBackgroundScreen (por encima de casillas y
    // anillos) y se recoloca en PositionMyBackgroundCheck.
    private void BuildMyBackgroundCheck()
    {
        const double cat = 53;
        _myBackgroundCheck = new Canvas { Width = cat, Height = cat, ZIndex = 5 };
        _myBackgroundCheck.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.Parse("#EBEBEB")),
            Data = Geometry.Parse($"M 0,0 L {cat.ToString(CultureInfo.InvariantCulture)},0 " +
                                  $"L {cat.ToString(CultureInfo.InvariantCulture)},{cat.ToString(CultureInfo.InvariantCulture)} Z"),
        });
        var tick = new Avalonia.Controls.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.Parse("#1A1A1A")),
            StrokeThickness = 2.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M 0,7 L 5,12 L 17,0"),
        };
        Canvas.SetLeft(tick, 24);
        Canvas.SetTop(tick, 11);
        _myBackgroundCheck.Children.Add(tick);
        MyBackgroundScreen.Children.Add(_myBackgroundCheck);
        PositionMyBackgroundCheck();
    }

    private void PositionMyBackgroundCheck()
    {
        if (_myBackgroundCheck is null)
        {
            return;
        }

        // Esquina superior derecha de la casilla activa (borde derecho = Left + ancho).
        Canvas.SetLeft(_myBackgroundCheck, MbTileLeft + MbTileWidth - 53);
        Canvas.SetTop(_myBackgroundCheck, MbTileTop0 + _myBackgroundActiveIndex * MbTilePitch);
    }

    // Aplica el fondo de la home segun el estado guardado: si hay color solido, muestra la capa de
    // color (SolidBackgroundLayer) tapando el video; si no, la oculta y se ve el video. Ademas apunta
    // el check de "fondo activo" de My background a la fuente correcta (0 = Solid colors, 4 = Dynamic
    // backgrounds) - los indices 1..3 (Achievement art / Custom image / Screenshots) aun no son
    // seleccionables como fondo.
    private void ApplyBackground()
    {
        if (_backgroundSolidHex is { } hex)
        {
            SolidBackgroundLayer.Fill = new SolidColorBrush(Color.Parse(hex));
            SolidBackgroundLayer.IsVisible = true;
            HomeBackgroundPoster.IsVisible = false;
            _myBackgroundActiveIndex = 0;
        }
        else
        {
            SolidBackgroundLayer.IsVisible = false;
            // Poster (primer fotograma) del video activo: se ve al instante y queda detras del video,
            // para que cuando el video se descargue/recargue no haya salto ni negro.
            if (ResolveHomePosterRelPath() is { } posterRel && LoadPoster(posterRel) is { } poster)
            {
                HomeBackgroundPoster.Source = poster;
                HomeBackgroundPoster.IsVisible = true;
                EvictPostersExceptOnScreen(); // solo se guarda el poster puesto; el resto se sueltan (diferido)
            }
            else
            {
                HomeBackgroundPoster.IsVisible = false;
            }
            _myBackgroundActiveIndex = 4;
        }

        // Carga o descarga el video segun si la home esta a la vista (y si el fondo es video).
        UpdateHomeVideoState();
        PositionMyBackgroundCheck();
    }

    // El video de fondo de la home se decodifica mientras Playfront esta en primer plano (la ventana activa)
    // y el fondo es un video (no color solido). Se DESCARGA del todo (desmonta el decodificador) solo al
    // perder el primer plano -entrar en un juego, alt-tab-, donde interesa liberar la GPU entera. En la
    // navegacion INTERNA (Ajustes, Personalization y sus subpantallas) no se descarga -para no disparar
    // el desmontaje delicado en cada entrada-, pero SI se PAUSA la decodificacion mientras esas pantallas
    // opacas tapan la home: no se ve, decodificarla solo gastaria GPU/bateria. Pausar es instantaneo de
    // deshacer (el decodificador sigue vivo) y no tiene el riesgo del desmontaje.
    private bool ShouldHomeVideoRun()
        => IsActive && _backgroundSolidHex is null;

    // La home esta TAPADA por una pantalla opaca a pantalla completa: Ajustes o Personalization (de la
    // que cuelgan TODAS sus subpantallas: My background, Dynamic backgrounds, colores...). Ademas de
    // ahorrar en esas pantallas, esto evita que en Dynamic corran DOS videos a la vez (el de la home,
    // tapado, + el de preview): con la home pausada, alli solo decodifica el de preview.
    private bool IsHomeCovered()
        => _inSettings || _inPersonalization || _inStore || _inYouTube || _inLibrary;

    private void UpdateHomeVideoState()
    {
        if (!ShouldHomeVideoRun())
        {
            SetVideoBackground(null);                   // descarga entera (juego en primer plano / color solido)
            return;
        }

        SetVideoBackground(ResolveHomeVideoPath());     // carga/reproduce (no-op si ya esta el correcto)

        // Con el video cargado: pausar la decodificacion si la home esta tapada, reanudarla si se ve.
        if (IsHomeCovered())
        {
            _videoBackground?.Pause();
        }
        else
        {
            _videoBackground?.Resume();
        }
    }

    // Ruta relativa (a Assets/Backgrounds) del poster (primer fotograma) del fondo de video activo (el
    // elegido o, por defecto, el primero de la biblioteca), o null si el fondo es un color solido.
    private string? ResolveHomePosterRelPath()
    {
        if (_backgroundSolidHex is not null)
        {
            return null;
        }

        var bg = _backgroundVideoRelPath is { } rel ? FindBackground(rel) : DefaultBackground();
        return bg?.PosterRelPath;
    }

    // "Restore default background": vuelve al fondo por defecto (el video dinamico por defecto,
    // olvidando cualquier video concreto o color elegido), lo guarda y recoloca el check.
    private void RestoreDefaultBackground()
    {
        _backgroundSolidHex = null;
        _backgroundVideoRelPath = null;
        BackgroundSettings.SaveDynamic();
        ApplyBackground();
    }

    // Entrar/salir del selector "Solid colors". Arranca el foco sobre el color aplicado (si el fondo
    // es uno de los 14); si no, el primero. Al cerrar (B u OK) vuelve a My background.
    private void EnterSolidColors()
    {
        _inSolidColors = true;
        _solidColorsIndex = Math.Max(0, SolidPaletteIndexOf(_backgroundSolidHex));
        SolidColorsUserName.Text = global::System.Environment.UserName;
        RefreshSolidAppliedCheck();
        SolidColorsScreen.IsVisible = true;
        UpdateSolidColorsSelection();
    }

    private void ExitSolidColors()
    {
        _inSolidColors = false;
        SolidColorsScreen.IsVisible = false;
    }

    private void MoveSolidColors(GamepadButton button)
    {
        var i = _solidColorsIndex; // 0..13 = recuadros, 14 = OK
        switch (button)
        {
            case GamepadButton.B:
                ExitSolidColors();
                return;
            case GamepadButton.A when i == 14: // OK -> cerrar
                ExitSolidColors();
                return;
            case GamepadButton.A when i < 14: // elegir este color: se pone de FONDO al instante
                ApplySolidColor(BackgroundSettings.SolidPalette[i]);
                return; // se queda en el selector para poder ver el cambio y probar otros
            case GamepadButton.Left when i < 14 && i % 7 > 0:
                i--;
                break;
            case GamepadButton.Right when i < 14 && i % 7 < 6:
                i++;
                break;
            case GamepadButton.Up when i >= 7 && i < 14:
                i -= 7;
                break;
            case GamepadButton.Up when i == 14: // OK -> fila 2, primera columna
                i = 7;
                break;
            case GamepadButton.Down when i < 7:
                i += 7;
                break;
            case GamepadButton.Down when i >= 7 && i < 14: // fila 2 -> OK
                i = 14;
                break;
            default:
                return;
        }

        _solidColorsIndex = i;
        UpdateSolidColorsSelection();
    }

    private void UpdateSolidColorsSelection()
    {
        for (var i = 0; i < _solidSwatchRings.Length; i++)
        {
            _solidSwatchRings[i].Classes.Set("selected", i == _solidColorsIndex);
        }

        var okSelected = _solidColorsIndex == 14;
        SolidOkRing.Classes.Set("selected", okSelected);
        SolidOkButton.Classes.Set("selected", okSelected);
    }

    // Aplica un color solido como fondo de la HOME EN CALIENTE y lo persiste. Se queda en el selector
    // (igual que el selector de acento) para poder elegir otro. OJO: solo cambia el fondo de la home;
    // el fondo del propio selector (y del resto de Ajustes) es fijo y NO se toca - el usuario lo pidio
    // asi ("background" solo afecta a la home). El feedback dentro del selector es la marca de
    // aplicado sobre el recuadro; el cambio de fondo se ve al volver a la home.
    private void ApplySolidColor(string hex)
    {
        _backgroundSolidHex = hex;
        BackgroundSettings.SaveSolid(hex);
        ApplyBackground();           // fondo de la HOME + check de My background
        RefreshSolidAppliedCheck();  // marca de aplicado sobre el recuadro elegido
    }

    // Genera los 14 recuadros de "Solid colors" + sus anillos + la marca de aplicado, dentro de
    // SolidSwatchHost. Rejilla 7x2, geometria medida del frame (ver constantes SolidSwatch*). Mismo
    // patron que BuildColorSwatches (el selector de acento).
    private void BuildSolidColorSwatches()
    {
        var palette = BackgroundSettings.SolidPalette;

        for (var i = 0; i < palette.Length; i++)
        {
            var swatch = new Border
            {
                Width = SolidSwatchW,
                Height = SolidSwatchH,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.Parse(palette[i])),
            };
            Canvas.SetLeft(swatch, SolidSwatchX(i));
            Canvas.SetTop(swatch, SolidSwatchY(i));
            SolidSwatchHost.Children.Add(swatch);
        }

        // Anillos despues de los recuadros para que queden por encima (su halo no lo tapa el vecino).
        for (var i = 0; i < palette.Length; i++)
        {
            var ring = new Border { Width = SolidSwatchW + 16, Height = SolidSwatchH + 16 };
            ring.Classes.Add("selectionRing");
            Canvas.SetLeft(ring, SolidSwatchX(i) - 8);
            Canvas.SetTop(ring, SolidSwatchY(i) - 8);
            SolidSwatchHost.Children.Add(ring);
            _solidSwatchRings[i] = ring;
        }

        // Marca de "aplicado": triangulo blanco (#EBEBEB) + check oscuro en la esquina superior
        // derecha del recuadro cuyo color es el fondo actual. Misma que la del selector de acento.
        _solidAppliedCheck = new Canvas { Width = 68, Height = 68, IsVisible = false, ZIndex = 5 };
        _solidAppliedCheck.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.Parse("#EBEBEB")),
            Data = Geometry.Parse("M 0,0 L 68,0 L 68,68 Z"),
        });
        var tick = new Avalonia.Controls.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.Parse("#1A1A1A")),
            StrokeThickness = 3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M 0,9 L 7,16 L 22,0"),
        };
        Canvas.SetLeft(tick, 30);
        Canvas.SetTop(tick, 14);
        _solidAppliedCheck.Children.Add(tick);
        SolidSwatchHost.Children.Add(_solidAppliedCheck);
    }

    // Coloca la marca de "aplicado" sobre el recuadro del fondo actual, o la oculta si el fondo es el
    // video (o un color que no esta en la paleta).
    private void RefreshSolidAppliedCheck()
    {
        if (_solidAppliedCheck is null)
        {
            return;
        }

        var idx = SolidPaletteIndexOf(_backgroundSolidHex);
        if (idx >= 0)
        {
            Canvas.SetLeft(_solidAppliedCheck, SolidSwatchX(idx) + SolidSwatchW - 68);
            Canvas.SetTop(_solidAppliedCheck, SolidSwatchY(idx));
            _solidAppliedCheck.IsVisible = true;
        }
        else
        {
            _solidAppliedCheck.IsVisible = false;
        }
    }

    private static double SolidSwatchX(int i) => SolidColX0 + i % 7 * SolidColPitch;

    private static double SolidSwatchY(int i) => i / 7 == 0 ? SolidRow0Y : SolidRow1Y;

    private static int SolidPaletteIndexOf(string? hex)
    {
        if (hex is null)
        {
            return -1;
        }

        var palette = BackgroundSettings.SolidPalette;
        for (var i = 0; i < palette.Length; i++)
        {
            if (string.Equals(palette[i], hex, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Entrar/salir de "Custom image" (pantalla SOLO VISUAL; ver CustomImageScreen en el XAML). No hay
    // navegacion interna: se cierra con B.
    private void EnterCustomImage()
    {
        _inCustomImage = true;
        CustomImageScreen.IsVisible = true;
    }

    private void ExitCustomImage()
    {
        _inCustomImage = false;
        CustomImageScreen.IsVisible = false;
    }

    private void MoveCustomImage(GamepadButton button)
    {
        if (button == GamepadButton.B)
        {
            ExitCustomImage();
        }
    }

    // Las 6 carpetas del frame "This Device", en orden de lectura (2 columnas x 3 filas).
    private static readonly string[] CustomImageFolders =
    {
        "Documents", "Downloads", "Favorites", "Music", "Pictures", "Videos",
    };

    // Genera las 6 carpetas (icono amarillo + nombre + fecha) y el recuadro de seleccion de
    // "Documents" dentro de FolderHost. Posiciones medidas del frame (2 columnas: x=222 y x=875;
    // filas cada 126 desde y=432).
    private void BuildCustomImageFolders()
    {
        // Recuadro de seleccion de "Documents" (primera carpeta): borde blanco fino, como en el frame.
        var docBorder = new Border
        {
            Width = 633,
            Height = 105,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
        };
        Canvas.SetLeft(docBorder, 197);
        Canvas.SetTop(docBorder, 413);
        FolderHost.Children.Add(docBorder);

        var dateBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));
        var folderBrush = new SolidColorBrush(Color.Parse("#F2D57E"));

        for (var i = 0; i < CustomImageFolders.Length; i++)
        {
            var iconX = i % 2 == 0 ? 222.0 : 875.0;
            var iconY = 432.0 + i / 2 * 126.0;

            var folder = new Avalonia.Controls.Shapes.Path
            {
                Fill = folderBrush,
                Data = Geometry.Parse(
                    "M4,11 Q4,7 8,7 L19,7 L25,13 L52,13 Q56,13 56,17 L56,40 Q56,44 52,44 L8,44 Q4,44 4,40 Z"),
            };
            Canvas.SetLeft(folder, iconX);
            Canvas.SetTop(folder, iconY);
            FolderHost.Children.Add(folder);

            var name = new TextBlock { Text = CustomImageFolders[i], FontSize = 30, Foreground = Brushes.White };
            Canvas.SetLeft(name, iconX + 116);
            Canvas.SetTop(name, iconY - 4);
            FolderHost.Children.Add(name);

            var date = new TextBlock { Text = "11/1/2023", FontSize = 24, Foreground = dateBrush };
            Canvas.SetLeft(date, iconX + 116);
            Canvas.SetTop(date, iconY + 40);
            FolderHost.Children.Add(date);
        }
    }

    // Entrar/salir de "Dynamic backgrounds". Arranca en la fila de miniaturas (foco 1), pestaña Games,
    // primera miniatura. Al cerrar con B vuelve a My background.
    private void EnterDynamic()
    {
        _inDynamic = true;
        _dynTab = 0;
        _dynIndex = 0;
        _dynFocus = 1;
        _dynPreviewTargetVideo = ""; // fuerza que UpdateDynPreview aplique el preview del primero
        _gamepad.RepeatEnabled = true; // aqui si: mantener izq/der acelera para pasar fondos rapido
        DynamicBackgroundsScreen.IsVisible = true;
        BuildDynRail();
        UpdateDynamic();
    }

    private void ExitDynamic()
    {
        _inDynamic = false;
        _gamepad.RepeatEnabled = false; // fuera de Dynamic, sin auto-repeat (como el resto de pantallas)
        DynamicBackgroundsScreen.IsVisible = false;
        _dynPreviewTimer?.Stop();
        // Se destruye el UNICO reproductor de preview (una sola vez, al salir; ya no en cada cambio de
        // fondo).
        if (_dynPreviewVideo != null)
        {
            _dynPreviewVideo.VideoReady -= OnDynPreviewReady;
            DynPreviewHost.Children.Remove(_dynPreviewVideo);
            _dynPreviewVideo = null;
        }
        DynPreviewPoster.IsVisible = false;
        DynPreviewHost.Opacity = 0; // deja el contenedor invisible para la proxima entrada
        _dynPreviewTargetVideo = "";
    }

    // Aplica un fondo dinamico (video) como fondo de la HOME EN CALIENTE y lo persiste. Se queda en la
    // pantalla (igual que el selector de colores) para poder elegir otro; la marca de "aplicado" se
    // mueve a la miniatura elegida. Al volver a la home con B se ve el video nuevo.
    private void ApplyDynamicBackground(DynBackground entry)
    {
        _backgroundSolidHex = null;
        _backgroundVideoRelPath = entry.VideoRelPath;
        BackgroundSettings.SaveVideo(entry.VideoRelPath);
        ApplyBackground();  // cambia el video de la home + coloca el check de My background en Dynamic
        BuildDynRail();     // repinta las miniaturas para mover la marca de "aplicado"
        UpdateDynamic();    // reaplica el desplazamiento del rail y refresca la etiqueta
    }

    private void MoveDynamic(GamepadButton button)
    {
        if (button == GamepadButton.B)
        {
            ExitDynamic();
            return;
        }

        if (_dynFocus == 0)
        {
            // Foco en las pestañas: Izq/Der cambia de pestaña (y reconstruye el rail); Abajo o A baja
            // a las miniaturas.
            switch (button)
            {
                case GamepadButton.Left when _dynTab > 0:
                    _dynTab--;
                    _dynIndex = 0;
                    BuildDynRail();
                    break;
                case GamepadButton.Right when _dynTab < _dynTabs.Length - 1:
                    _dynTab++;
                    _dynIndex = 0;
                    BuildDynRail();
                    break;
                case GamepadButton.Down:
                case GamepadButton.A:
                    _dynFocus = 1;
                    break;
                default:
                    return;
            }
        }
        else
        {
            // Foco en las miniaturas: Izq/Der mueve la seleccion; Arriba sube a las pestañas.
            switch (button)
            {
                case GamepadButton.Left when _dynIndex > 0:
                    _dynIndex--;
                    break;
                case GamepadButton.Right when _dynIndex < DynTabCounts[_dynTab] - 1:
                    _dynIndex++;
                    break;
                case GamepadButton.Up:
                    _dynFocus = 0;
                    break;
                case GamepadButton.A:
                    // Aplica el fondo seleccionado (si esa miniatura ya tiene fondo real; las
                    // placeholder aun no hacen nada).
                    if (DynEntry(_dynTab, _dynIndex) is { } chosen)
                    {
                        ApplyDynamicBackground(chosen);
                    }
                    return;
                default:
                    return;
            }
        }

        UpdateDynamic();
    }

    // (Re)genera las miniaturas placeholder de la pestaña actual dentro de DynRailHost, colocadas a
    // x = i*pitch. El desplazamiento del rail lo pone UpdateDynamic via RenderTransform.
    private void BuildDynRail()
    {
        DynRailHost.Children.Clear();
        var count = DynTabCounts[_dynTab];
        // Video de fondo aplicado ahora mismo (el elegido, o el por defecto): su miniatura lleva la ✓.
        var appliedVideo = _backgroundSolidHex is null
            ? (_backgroundVideoRelPath ?? DefaultBackground()?.VideoRelPath)
            : null;
        for (var i = 0; i < count; i++)
        {
            var tile = new Border { ClipToBounds = true };
            tile.Classes.Add("dynThumb");

            var entry = DynEntry(_dynTab, i);
            if (entry != null)
            {
                // Miniatura real: su poster, pero decodificado PEQUEÑO (tamaño de miniatura, no 1080p).
                // Cargar las decenas de posters a resolucion completa a la vez petaba la pagina
                // (memoria + composicion). El poster grande (LoadPoster) se usa solo a pantalla
                // completa y de uno en uno (home / preview).
                if (LoadThumbnail(entry.PosterRelPath) is { } thumb)
                {
                    tile.Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };
                }

                // Marca de "aplicado" si esta miniatura es justo el fondo activo de la home.
                if (appliedVideo == entry.VideoRelPath)
                {
                    tile.Child = BuildDynAppliedBadge();
                }
            }

            Canvas.SetLeft(tile, i * DynThumbPitch);
            Canvas.SetTop(tile, 0);
            DynRailHost.Children.Add(tile);
        }
    }

    // Carga (con cache) el poster a RESOLUCION COMPLETA, para mostrarlo a pantalla completa (fondo de la
    // home). Se usa de uno en uno; null si el archivo no existe.
    private Avalonia.Media.Imaging.Bitmap? LoadPoster(string posterRelPath)
    {
        if (_dynPosterCache.TryGetValue(posterRelPath, out var cached))
        {
            return cached;
        }

        var path = BackgroundFullPath(posterRelPath);
        if (!File.Exists(path))
        {
            return null;
        }

        var bmp = new Avalonia.Media.Imaging.Bitmap(path);
        _dynPosterCache[posterRelPath] = bmp;
        return bmp;
    }

    // Deja en cache SOLO el poster que se ve ahora mismo en la home; suelta los demas. Se llama justo
    // DESPUES de fijar HomeBackgroundPoster.Source al nuevo, de modo que "el que se ve" ya es el nuevo y
    // el anterior se puede soltar. La liberacion es diferida (ver ScheduleDispose).
    private void EvictPostersExceptOnScreen()
    {
        var onScreen = HomeBackgroundPoster.Source;
        var toRemove = new global::System.Collections.Generic.List<string>();
        foreach (var kv in _dynPosterCache)
        {
            if (!ReferenceEquals(kv.Value, onScreen))
            {
                toRemove.Add(kv.Key);
            }
        }
        foreach (var key in toRemove)
        {
            if (_dynPosterCache.Remove(key, out var bmp))
            {
                ScheduleDispose(bmp);
            }
        }
    }

    // Libera un poster un instante DESPUES (no en el acto): la foto recien quitada de pantalla puede
    // seguir en vuelo en la GPU ~1 fotograma, y liberarla justo entonces casca. 300 ms (unos 20-30
    // fotogramas) es de sobra para que ya no este en uso.
    private void ScheduleDispose(Avalonia.Media.Imaging.Bitmap bmp)
        => DispatcherTimer.RunOnce(bmp.Dispose, TimeSpan.FromMilliseconds(300));

    // Cache de miniaturas PEQUEÑAS (versiones a ~320px del poster) para el rail. Aparte del cache del
    // poster grande: el rail tiene decenas de miniaturas y cargarlas a 1080p a la vez petaba la pagina.
    private readonly global::System.Collections.Generic.Dictionary<string, Avalonia.Media.Imaging.Bitmap> _dynThumbCache = new();

    // Carga (con cache) la miniatura de un poster decodificada a ~320px de ancho (la casilla es de
    // 262px). Decodificar pequeño es ~30x menos memoria y mucho mas rapido que el 1080p completo.
    private Avalonia.Media.Imaging.Bitmap? LoadThumbnail(string posterRelPath)
    {
        if (_dynThumbCache.TryGetValue(posterRelPath, out var cached))
        {
            return cached;
        }

        var path = BackgroundFullPath(posterRelPath);
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        var bmp = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 320);
        _dynThumbCache[posterRelPath] = bmp;
        return bmp;
    }

    // Marca de "aplicado" del fondo actual, calcada de Xbox (selected.png): TODA la miniatura se
    // oscurece con un velo + un ✓ blanco centrado (trazo fino, puntas redondeadas). Rellena la casilla.
    private static Control BuildDynAppliedBadge()
    {
        var host = new Grid();

        // Velo oscuro sobre toda la miniatura (redondeado igual que la casilla).
        host.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#80000000")),
            CornerRadius = new CornerRadius(9),
        });

        // ✓ blanco centrado.
        host.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Stroke = Brushes.White,
            StrokeThickness = 4,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Data = Geometry.Parse("M 0,13 L 14,27 L 46,0"),
        });

        return host;
    }

    private void UpdateDynamic()
    {
        // Pestaña activa (blanco+negrita) + subrayado bajo ella.
        for (var i = 0; i < _dynTabs.Length; i++)
        {
            _dynTabs[i].Classes.Set("active", i == _dynTab);
        }

        // El subrayado (bajo la pestaña activa) y el nombre del fondo se colocan segun su ANCHO REAL,
        // que solo se conoce tras la pasada de layout (la negrita cambia el ancho de la pestaña; cada
        // fondo tiene un nombre de distinto ancho). Por eso se recolocan con Post (prioridad Loaded =
        // despues de medir/colocar).
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTabUnderline();
            UpdateDynLabelPosition();
        }, DispatcherPriority.Loaded);

        // Carrusel: se desplaza el rail para que la miniatura seleccionada quede en DynRailSelX.
        DynRailHost.RenderTransform = TransformOperations.Parse(
            $"translateX({(DynRailSelX - _dynIndex * DynThumbPitch).ToString(CultureInfo.InvariantCulture)}px)");

        // El anillo laser (fijo) solo se ve cuando el foco esta en las miniaturas; al subir a las
        // pestañas se oculta, y asi se ve donde esta el foco.
        DynRailRing.IsVisible = _dynFocus == 1;

        // Nombre del fondo: el real si esa miniatura ya tiene fondo, o el placeholder si no.
        DynLabel.Text = DynEntry(_dynTab, _dynIndex)?.Name ?? $"Background {_dynIndex + 1}";
        DynHintAction.Text = DynHintActions[_dynTab];

        // Preview a pantalla completa del fondo enfocado (aunque no este aplicado).
        UpdateDynPreview();
    }

    // Ajusta el preview de fondo al wallpaper ENFOCADO ahora mismo (_dynTab/_dynIndex): pone su poster
    // al instante y programa su video tras un respiro. Si la miniatura enfocada no cambia de wallpaper
    // (p.ej. al pasar el foco de pestañas a rail y viceversa), no hace nada. Si es una miniatura
    // placeholder (sin fondo real), deja la base oscura.
    private void UpdateDynPreview()
    {
        var entry = DynEntry(_dynTab, _dynIndex);
        var wantVideo = entry != null ? BackgroundFullPath(entry.VideoRelPath) : null;

        if (wantVideo == _dynPreviewTargetVideo)
        {
            return; // mismo wallpaper enfocado: nada que cambiar
        }
        _dynPreviewTargetVideo = wantVideo;

        // Imagen de carga del fondo enfocado (se vera DESENFOCADA), al instante y SIN decodificar nada
        // nuevo: la MINIATURA pequeña que ya esta cacheada del rail. Con el blur no se distingue de una
        // version mayor, asi que no gastamos RAM ni tiempo en un poster mediano aparte. Se funde el video
        // a invisible mientras; reaparece con fundido cuando su primer fotograma esta listo
        // (OnDynPreviewReady). Sin miniatura (pestaña placeholder) -> base oscura.
        if (entry != null && LoadThumbnail(entry.PosterRelPath) is { } quick)
        {
            DynPreviewPoster.Source = quick;
            DynPreviewPoster.IsVisible = true;
        }
        else
        {
            DynPreviewPoster.IsVisible = false;
        }
        // Funde el video a invisible: mientras carga el nuevo, se ve el poster (desenfocado) de
        // abajo. Se vuelve a fundir a 1 cuando el nuevo primer fotograma esta listo (OnDynPreviewReady).
        DynPreviewHost.Opacity = 0;

        // Cambiar el video del preview tras un pequeño respiro (para no ir cambiando en cada paso al
        // pasar rapido). No destruye/crea el reproductor: solo le cambia la fuente (ver el Tick).
        _dynPreviewTimer?.Stop();
        if (wantVideo != null)
        {
            _dynPreviewTimer?.Start();
        }
    }

    // Crea el ÚNICO reproductor de preview la primera vez que hace falta (con el video que toca en ese
    // momento). Empieza oculto; se revela en OnDynPreviewReady cuando ya se ve su primer fotograma.
    private void EnsureDynPreviewControl()
    {
        if (_dynPreviewVideo != null)
        {
            return;
        }

        // El video en si siempre esta visible; quien controla que se vea o no (y el fundido) es la
        // opacidad de DynPreviewHost, que envuelve a este control. Empieza el host en Opacity=0.
        _dynPreviewVideo = new HardwareVideoBackgroundControl(_dynPreviewTargetVideo!)
        {
            Width = 1920,
            Height = 1080,
        };
        _dynPreviewVideo.VideoReady += OnDynPreviewReady;
        DynPreviewHost.Children.Add(_dynPreviewVideo);
    }

    // El reproductor avisa (VideoReady) de que ya se ve el primer fotograma del video actual: se revela
    // (encima del poster). Solo si seguimos en la pantalla y sigue habiendo un video enfocado.
    private void OnDynPreviewReady()
    {
        if (_inDynamic && _dynPreviewVideo != null && _dynPreviewTargetVideo != null)
        {
            // Funde el video nitido por encima del poster borroso ("borroso -> nitido").
            DynPreviewHost.Opacity = 1;

            // Terminado el cruce, el poster borroso queda totalmente tapado por el video opaco:
            // ocultarlo para que Avalonia no siga re-desenfocando una imagen a pantalla completa
            // cada frame detras del video (Avalonia no descarta lo que queda oculto). Solo si no
            // hemos cambiado de fondo entretanto (se recomprueba el video enfocado).
            var settledVideo = _dynPreviewTargetVideo;
            DispatcherTimer.RunOnce(() =>
            {
                if (_inDynamic && _dynPreviewTargetVideo == settledVideo && DynPreviewHost.Opacity >= 1)
                    DynPreviewPoster.IsVisible = false;
            }, TimeSpan.FromMilliseconds(240));
        }
    }

    // Coloca el subrayado bajo la palabra de la pestaña activa con su ANCHO REAL (Bounds tras el
    // layout, que ya refleja la negrita) y alineado con ella. La forma de pastilla (Height 6, radio
    // 3 en el XAML) le da las puntas totalmente redondeadas de linea.png.
    private void UpdateTabUnderline()
    {
        var tab = _dynTabs[_dynTab];
        if (tab.Bounds.Width <= 0)
        {
            return;
        }

        Canvas.SetLeft(DynTabUnderline, tab.Bounds.X);
        DynTabUnderline.Width = tab.Bounds.Width;
    }

    // Centro horizontal de la miniatura seleccionada (fija en DynRailSelX, ancho 262 del estilo
    // dynThumb): el nombre del fondo se centra aqui.
    private const double DynThumbSelCenterX = DynRailSelX + 131; // 131 (borde izq) + 262/2

    // Centra el nombre del fondo sobre la miniatura seleccionada (su ancho real solo se conoce tras el
    // layout, y cambia con cada nombre).
    private void UpdateDynLabelPosition()
    {
        if (DynLabel.Bounds.Width <= 0)
        {
            return;
        }

        Canvas.SetLeft(DynLabel, DynThumbSelCenterX - DynLabel.Bounds.Width / 2);
    }

    private int NearestColumn(int row, double targetCenterX)
    {
        var centers = _rowCenters[row];
        var bestIndex = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < centers.Length; i++)
        {
            var distance = Math.Abs(centers[i] - targetCenterX);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private void UpdateSelection()
    {
        for (var r = 0; r < _rows.Length; r++)
        {
            for (var c = 0; c < _rows[r].Length; c++)
            {
                var isSelected = r == _row && c == _col;
                _rows[r][c].Classes.Set("selected", isSelected);
                if (c < _homeRings[r].Length)
                {
                    _homeRings[r][c].Classes.Set("selected", isSelected);
                }
            }
        }

        ApplyTileTransforms();

        // El degradado oscuro de arriba solo hace falta mientras el foco esta en esa fila
        // (avatar/nav) - en los juegos de abajo el fondo se queda tal cual, sin oscurecer.
        TopBarGradient.Opacity = _row == 0 ? 1 : 0;

        // El texto bajo el cluster de navegacion SOLO se ve mientras el foco esta en esa fila
        // (fila 0). Al bajar a los juegos se oculta con fundido (Opacity 0), no se queda con el
        // ultimo texto puesto - corregido despues (en Xbox real tampoco persiste
        // sobre los juegos). El Text/posicion solo se actualiza en la fila 0; al salir de ella se
        // deja el ultimo texto pero invisible, asi no parpadea mientras se desvanece.
        if (_row == 0)
        {
            NavLabel.Text = NavLabels[_col];
            Canvas.SetLeft(NavLabel, _rowCenters[0][_col] - NavLabel.Width / 2);
        }

        NavLabel.Opacity = _row == 0 ? 1 : 0;
    }

    // Cuanto crece la casilla seleccionada, por fila.
    //
    // Fila 1 (juegos): 1.618 - el numero aureo, y no es casualidad. Medido el 2026-07-17 sobre 5
    // capturas reales de la home de Xbox con distintos juegos seleccionados: las 4 que se pudieron
    // medir dan 1.6127, 1.6179, 1.6188 y 1.6220. La medida anterior (1.23) era un apaño: se habia
    // elegido "lo maximo que cabe sin tocar a la casilla vecina" porque entonces creiamos que las
    // vecinas no se movian. Se movian.
    //
    // Fila 2 (los 4 recuadros anchos): 1.12, heredado. NO esta medido - las capturas del usuario
    // solo cubren la fila de juegos. Con 1.618 esa fila no cabria en pantalla, asi que hasta tener
    // una referencia se queda como estaba.
    private static readonly double[] SelectedTileScale = { 0, 1.618, 1.12 };

    // Tamaño base (sin seleccionar) de las casillas de cada fila, tal y como estan declaradas en el
    // XAML. Hace falta guardarlo porque la seleccion cambia Width/Height de verdad (no es una
    // transformacion), asi que al deseleccionar hay que saber a que volver.
    private static readonly double[] BaseTileWidth = { 0, 154, 400 };
    private static readonly double[] BaseTileHeight = { 0, 154, 230 };

    // Cuanto sobresale el anillo de seleccion respecto a su casilla, por cada lado. Remedido el
    // 2026-07-17 (remedido sobre el video real): el trazo brillante de Xbox va SEPARADO de la casilla
    // por un pequeño hueco (~4.5px) relleno del tono oscuro del glow. Con el trazo fino (2.5) e
    // inflado 7, el borde interior del trazo cae a ~4.5px de la casilla -> ese es el hueco, que la
    // sombra "inset" verde-oscura rellena. Par a proposito (simetria bajo UseLayoutRounding).
    private const double RingInflate = 8;

    // Coloca las casillas de la home segun la seleccion, replicando lo que hace Xbox. La geometria
    // del estado SELECCIONADO se midio sobre un video de la home real; el estado en REPOSO (nada
    // seleccionado) el video no lo muestra nunca; se midio sobre una captura aparte.
    //
    // Dos estados por fila:
    //   - REPOSO (esta fila no tiene el foco): las casillas estan repartidas para LLENAR el ancho,
    //     con los extremos en 110 y 1824 (alineados con la fila de abajo). Esas son sus posiciones
    //     del XAML, asi que en reposo translateX = 0. El hueco entre casillas es "grande" (41).
    //   - SELECCIONADA: la elegida crece (numero aureo) anclada por su esquina INFERIOR IZQUIERDA
    //     (sale gratis: van con Canvas.Left + Canvas.Bottom, subirle el tamaño la hace crecer arriba
    //     y a la derecha). El resto se COMPRIME hacia ella (el hueco baja a ~29) manteniendo los DOS
    //     extremos clavados en 110 y 1824. No se amontonan al principio ni se sale nada.
    //
    // El desplazamiento de cada casilla i respecto a su sitio de reposo, con la casilla 'sel'
    // seleccionada, es:  translateX(i) = -i*crecimiento/(n-1) + (i > sel ? crecimiento : 0)
    // Los dos extremos dan 0 (i=0 -> 0; i=n-1 -> -crecimiento + crecimiento = 0), o sea que no se
    // mueven. Verificado contra el video: reproduce las 9 posiciones de las casillas dentro de 1px.
    private void ApplyTileTransforms()
    {
        for (var r = 1; r < _rows.Length; r++)
        {
            var row = _rows[r];
            var ringRow = _homeRings[r];
            var baseW = BaseTileWidth[r];
            var baseH = BaseTileHeight[r];
            var selW = baseW * SelectedTileScale[r];
            var selH = baseH * SelectedTileScale[r];
            var growth = selW - baseW;
            var n = row.Length;
            var rowFocused = _row == r;

            for (var c = 0; c < row.Length; c++)
            {
                var isSelected = rowFocused && c == _col;
                var w = isSelected ? selW : baseW;
                var h = isSelected ? selH : baseH;

                row[c].Width = w;
                row[c].Height = h;
                ringRow[c].Width = w + 2 * RingInflate;
                ringRow[c].Height = h + 2 * RingInflate;

                // El anillo se coloca a partir de la casilla y de RingInflate (una sola fuente de
                // verdad), en vez de con Canvas.Left/Bottom fijos en el XAML sincronizados a mano.
                // Canvas.GetLeft/GetBottom dan la posicion BASE de la casilla (el empuje lateral es un
                // RenderTransform que no toca estas propiedades adjuntas) y el anillo hereda el mismo
                // translateX mas abajo, asi que queda centrado sobre su casilla al crecer.
                Canvas.SetLeft(ringRow[c], Canvas.GetLeft(row[c]) - RingInflate);
                Canvas.SetBottom(ringRow[c], Canvas.GetBottom(row[c]) - RingInflate);

                // En reposo (fila sin foco) todas se quedan en su sitio del XAML (repartido).
                var offset = rowFocused
                    ? -c * growth / (n - 1) + (c > _col ? growth : 0)
                    : 0;
                var transform = TransformOperations.Parse(
                    $"translateX({offset.ToString(CultureInfo.InvariantCulture)}px)");
                row[c].RenderTransform = transform;
                ringRow[c].RenderTransform = transform;
            }
        }
    }
}
