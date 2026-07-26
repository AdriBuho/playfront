using System;
using System.Collections.Generic;
using System.Globalization;
using Playfront.App.Input;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Playfront.App.Views;

/// <summary>
/// Pantalla de la Tienda (1:1 con la Store de Xbox). Vive en su propia vista para cargarse BAJO
/// DEMANDA: MainWindow la crea al entrar (EnterStore) y la libera al salir (ExitStore). Ver
/// Arquitectura de rendimiento: nada se construye antes de que el usuario lo abra.
///
/// Se construye por fases. Hecho: fondo + rail estrecho + maqueta de la Store Home + NAVEGACION del
/// contenido (foco direccional con el anillo verde). Pendiente: arte real de los tiles y la doble
/// barra lateral. Lo que SALE de la pantalla se avisa por eventos (ExitRequested).
/// </summary>
public partial class StoreView : UserControl
{
    /// <summary>Se pide cerrar la Tienda y volver a la home (B en el nivel superior de la Tienda).</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Se pide abrir una PAGINA DE CATEGORIA desde la 2a columna de la barra lateral (A sobre una
    /// subcategoria que ya tenga pantalla). De momento solo existe la de "Music apps".
    /// </summary>
    public event Action<string>? CategoryRequested;

    /// <summary>Subcategorias que ya tienen pantalla construida.</summary>
    private static bool HasPage(string sub) => sub == "Music apps";

    // Un elemento navegable: su anillo de seleccion (para encender/apagar el "selected") + su
    // rectangulo en coordenadas ABSOLUTAS del lienzo 1920x1080 (para el calculo direccional). El
    // rectangulo es el del TILE, no el del anillo (el anillo va inflado 8px alrededor).
    private sealed record FocusItem(Border Ring, double X, double Y, double W, double H)
    {
        public double CenterX => X + W / 2;
        public double CenterY => Y + H / 2;
        public double Right => X + W;
        public double Bottom => Y + H;
    }

    private readonly List<FocusItem> _items = new();
    private int _focus;

    // Fila "Games": caratulas placeholder (portrait). Geometria en coordenadas relativas al
    // GamesRailHost (que esta en Canvas.Top=985); para el calculo direccional se pasan a absolutas.
    // Carátulas APAISADAS (medido en tienda home.png: son landscape, no portrait). Empiezan en el
    // borde del contenido (x=240 relativo al host, que está en Canvas.Left=240 -> 240 absoluto). El
    // rail se sale por la derecha y se recorta abajo, como en la referencia.
    private const int GamesCoverCount = 6;
    private const double GamesRailTop = 948;
    private const double CoverW = 306;
    private const double CoverH = 172;
    private const double CoverPitch = 325;

    // ===== Carrusel del HERO (spotlight) =====
    // 5 paginas de EJEMPLO. Cada una tiene un COLOR plano de placeholder (mas adelante iran los
    // artworks reales de los juegos). LB/RB pasan de pagina -pero SOLO con el foco sobre el hero
    // (index 0)-, y la tira de paginas se DESLIZA hacia el lado del cambio (efecto carrusel movil).
    private sealed record HeroPage(string Title, string Subtitle, string Color);

    private static readonly HeroPage[] HeroPages =
    {
        new("Ori and the Will of the Wisps", "Embark on an all-new adventure", "#2E5E8C"),
        new("Halo Infinite", "Master Chief's greatest journey", "#3C7A3C"),
        new("Forza Horizon 5", "Your ultimate horizon adventure", "#8C3C7A"),
        new("Starfield", "Explore the stars", "#B5762A"),
        new("Sea of Thieves", "A shared-world pirate adventure", "#2A8C8C"),
    };

    private const double HeroWidth = 1098; // ancho del hero (= una pagina del filmstrip)
    private int _heroPage;                 // pagina logica actual (0..N-1)
    private Ellipse[] _heroDots = null!;
    private static readonly IBrush DotActive = Brushes.White;
    private static readonly IBrush DotInactive = new SolidColorBrush(Color.Parse("#7AFFFFFF"));

    // Carrusel INFINITO por clones: el filmstrip lleva un clon de la ULTIMA pagina al principio y uno
    // de la PRIMERA al final -> [clon(N-1), 0, 1, ..., N-1, clon(0)]. La pagina logica p esta en el
    // indice de filmstrip p+1. Al pasar del extremo se desliza hacia el clon (continuo, sin rebobinar)
    // y, acabada la animacion, se SALTA sin animacion a la pagina real equivalente. Ese salto se cubre
    // con _heroWrapping (ignora pulsaciones durante el ~breve salto) + un timer.
    private Transitions? _heroTransitions; // la transicion definida en el XAML (para quitarla en el salto)
    private bool _heroWrapping;
    private DispatcherTimer? _heroWrapTimer;

    // ===== DOBLE BARRA LATERAL =====
    // Estado de la barra: cerrada (navegando el contenido), o abierta con el foco en la columna de
    // ETIQUETAS (col0) o en la de SUBCATEGORIAS (col1). Se abre al ir a la IZQUIERDA desde el
    // contenido cuando ya no hay nada mas a la izquierda; se cierra con B/Izquierda.
    private enum SidebarState { Closed, Col0, Col1 }
    private SidebarState _sidebarState = SidebarState.Closed;
    private const int ActiveSectionRow = 2; // Home: la seccion de la Tienda en la que estamos ahora mismo
    private int _col0Focus;  // fila enfocada en la columna de etiquetas (indice en NavRows)
    private int _col1Focus;  // subcategoria enfocada en la 2a columna

    // Una fila del rail expandido: su TEXTO, el CENTRO vertical de la fila (= centro del icono, en el
    // lienzo 1920x1080) y su lista de SUBCATEGORIAS (vacia = no despliega 2a columna).
    //
    // Las secciones son EXACTAMENTE las de la captura "tienda barra lateral.png": Search, Home, Games,
    // Apps, Hardware | Lists, Cart, Redeem | CLOSE. (Antes habia Movies & TV y Settings, que en esa
    // version de la Store no existen, y "Wishlist" se llama "Lists".)
    //
    // SOLO se rellenan las listas de subcategorias que se ven ABIERTAS en alguna captura de referencia:
    //   - APPS, de "tienda barra lateral.png" (la columna lleva el titulo "Apps").
    //   - HOME, de "home.png" (titulo "Home"): Store Home / Deals / Subscriptions.
    //   - GAMES, de "games.png" (titulo "Games"): 11 entradas.
    // Las demas secciones se quedan SIN subcategorias a proposito: no hay captura de ellas y no se
    // inventan.
    private sealed record NavRow(string Label, double Center, string[] Subs, bool IsClose = false);
    private static readonly string[] NoSub = Array.Empty<string>();
    private static readonly NavRow[] NavRows =
    {
        new("(perfil)",      63,   NoSub),  // 0 perfil: el nombre lo pinta SidebarProfileName con el usuario de Windows
        new("Search",        150,  NoSub),  // 1
        new("Home",          233,  new[] { "Store Home", "Deals", "Subscriptions" }), // 2
        // Games (de "games.png"): las 11 entradas VISIBLES en la captura. La original lleva ademas una
        // flecha abajo (hay mas lista por debajo) que aqui NO va: la lista acaba en
        // "Most played" y no hay scroll.
        new("Games",         316,  new[]
        {
            "Games Home", "Add-ons", "Accessibility in games", "Subscriptions", "New games",
            "Coming soon", "Top paid", "Optimized for Series X|S", "Game demos", "Top free",
            "Most played",
        }), // 3
        // "Music apps" es la unica que no va literal de la captura (alli pone "Popular music apps"):
        // aqui va a secas, sin el "Popular".
        new("Apps",          399,  new[] { "Apps Home", "Entertainment apps", "Apps for gamers", "Apps with trials", "Popular apps", "Music apps" }), // 4
        new("Hardware",      482,  NoSub),  // 5
        new("Lists",         577,  NoSub),  // 6 (tras la divisoria)
        new("Cart",          660,  NoSub),  // 7
        new("Redeem",        743,  NoSub),  // 8
        new("CLOSE",         1013, NoSub, IsClose: true), // 9
    };

    // Geometria de la col. de SUBCATEGORIAS (medida en barra lateral 1, llevada al lienzo): tiles
    // alineados a la izquierda del panel, primera fila centrada en 150 (= fila Search) y pitch 83
    // (mismo que las etiquetas). Relleno gris medido (66,70,71).
    private const double SubTileLeft = 420;
    private const double SubTileWidth = 340;
    private const double SubTileHeight = 70;
    private const double SubRowTop0 = 150;   // centro de la primera fila
    private const double SubRowPitch = 83;
    private static readonly IBrush SubTileFill = new SolidColorBrush(Color.Parse("#424647"));
    private static readonly IBrush LabelIdle = new SolidColorBrush(Color.Parse("#B9BEC1"));

    private readonly List<TextBlock> _sidebarLabels = new();
    private readonly List<Border> _subRings = new();
    private readonly List<Border> _subTiles = new(); // tiles + anillos de la 2a columna (para la cascada)

    // ===== Tiempos de la animacion de la barra (ms) =====
    // La SALIDA es mas corta que la ENTRADA a proposito: al abrir se quiere la sensacion de que la
    // barra "se despliega"; al cerrar, que se quite de en medio rapido y devuelva el contenido.
    private const int Col0InMs = 170;
    private const int LabelInMs = 260, LabelStepMs = 18;  // cada etiqueta entra 18 ms despues de la anterior
    private const int LabelOutMs = 180, LabelOutStepMs = 16; // ...y al cerrar sale igual, pero AL REVES
    private const int Col1InMs = 320, Col1OutMs = 190;
    private const int SubTileInMs = 240, SubTileStepMs = 14; // cascada de los tiles de subcategoria
    private const int SubTileOutMs = 180;                    // ...y su cascada inversa al cerrar
    private const double SubTileSlideX = -34;                // desde donde entra cada tile
    private const int BarSlideMs = 200;  // la barrita del activo deslizandose de fila a fila
    private const int RailToneMs = 220;  // el rail cambiando de tono colapsado <-> expandido
    private const double LabelSlideX = -26; // desde donde entra cada etiqueta (hacia los iconos)
    private const double Col1SlideX = -170; // la 2a columna emerge desde detras de las etiquetas

    // Al cerrar, primero se van las FILAS (en cascada inversa: la de abajo primero, subiendo) y solo
    // despues se funden los PANELES; si no, el panel desapareceria antes que sus etiquetas y estas se
    // verian flotando un instante sobre el contenido.
    private const int PanelOutDelayMs = 190, SidebarOutMs = 160;

    // Transiciones de entrada y de SALIDA (duraciones distintas). Se guardan para poder anularlas un
    // instante al fijar el estado inicial sin animar (mismo truco que el carrusel del hero).
    private Transitions _col0In = null!;
    private Transitions _col1In = null!, _col1Out = null!, _col1Hide = null!;
    private Transitions _hostOut = null!;
    private Transitions[] _labelIn = null!, _labelOut = null!; // una por fila (llevan su retardo dentro)
    private bool _subShown;          // si la 2a columna esta desplegada ahora (para animarla solo al aparecer)
    private string[]? _lastSubs;     // ultima lista pintada en la 2a columna (para no re-animar si no cambia)
    private DispatcherTimer? _closeTimer; // remata el cierre (ocultar la capa) cuando acaba la salida

    public StoreView()
    {
        InitializeComponent();

        _heroDots = new[] { HeroDot0, HeroDot1, HeroDot2, HeroDot3, HeroDot4 };
        BuildHeroPages();
        _heroTransitions = HeroFilmstrip.Transitions; // guarda la transicion del XAML
        SetFilm(1, animate: false);                   // arranca en la pagina 0 (indice 1 por el clon)
        UpdateDots();
        // Timer del "salto" del wrap: tras la animacion (~300 ms), salta sin animar a la pagina real.
        _heroWrapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _heroWrapTimer.Tick += (_, _) =>
        {
            _heroWrapTimer!.Stop();
            SetFilm(_heroPage + 1, animate: false);
            _heroWrapping = false;
        };

        // Nombre del perfil = el usuario de WINDOWS (el mismo de C:\Users\...). Cuando Playfront tenga
        // cuenta propia/Xbox, este es el sitio donde cambiarlo.
        SidebarProfileName.Text = string.IsNullOrWhiteSpace(Environment.UserName) ? "Player" : Environment.UserName;

        BuildGamesRail();
        BuildFocusItems();
        BuildSidebarLabels();
        BuildSidebarTransitions();

        // Foco inicial en el HERO (spotlight), el punto de entrada natural arriba-izquierda. En la
        // captura salia SEARCH porque era lo que estaba enfocado en ese momento, no un estado fijo.
        _focus = 0;
        UpdateSelection();
    }

    // Crea las etiquetas de texto de la col0 (a la derecha de los iconos del rail, ya dibujados). Una
    // vez; el color (gris/blanco) y la barra del activo se actualizan al mover el foco. El nombre del
    // perfil (fila 0) lo pinta SidebarProfileName en el XAML, asi que aqui esa fila no lleva etiqueta.
    private void BuildSidebarLabels()
    {
        for (var i = 0; i < NavRows.Length; i++)
        {
            if (i == 0)
            {
                _sidebarLabels.Add(SidebarProfileName); // el nombre del perfil ya esta en el XAML
                continue;
            }

            var label = new TextBlock
            {
                Text = NavRows[i].Label,
                FontSize = 24,
                Foreground = LabelIdle,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Canvas.SetLeft(label, 140);
            Canvas.SetTop(label, NavRows[i].Center - 16);
            SidebarLabelHost.Children.Add(label);
            _sidebarLabels.Add(label);
        }

        // La barrita del activo se coloca UNA vez en la fila 0 y a partir de ahi se mueve con
        // translateY (una posicion animable); asi puede DESLIZARSE de fila a fila en vez de saltar.
        Canvas.SetTop(SidebarActiveBar, NavRows[0].Center - 14);
    }

    // Transiciones de una etiqueta. ENTRADA: desliza desde los iconos + funde, con retardo segun su
    // fila -> cascada de ARRIBA a ABAJO. SALIDA: lo mismo AL REVES (la fila de abajo se va primero y
    // la cascada sube). Ademas funde el color gris<->blanco al cambiar de fila enfocada.
    private static Transitions LabelTransitions(int step, bool entering) => new()
    {
        new TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(entering ? LabelInMs : LabelOutMs),
            Delay = TimeSpan.FromMilliseconds(step * (entering ? LabelStepMs : LabelOutStepMs)),
            Easing = entering ? new CubicEaseOut() : new CubicEaseIn(),
        },
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(entering ? LabelInMs : LabelOutMs),
            Delay = TimeSpan.FromMilliseconds(step * (entering ? LabelStepMs : LabelOutStepMs)),
        },
        new BrushTransition
        {
            Property = TextBlock.ForegroundProperty,
            Duration = TimeSpan.FromMilliseconds(140),
        },
    };

    // Genera las caratulas de la fila Games + sus anillos dentro de GamesRailHost. Placeholders
    // grises por ahora; se pueblan con arte real despues.
    private void BuildGamesRail()
    {
        for (var i = 0; i < GamesCoverCount; i++)
        {
            var x = i * CoverPitch;

            var tile = new Border { Width = CoverW, Height = CoverH };
            tile.Classes.Add("storeTile");
            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, 0);
            GamesRailHost.Children.Add(tile);

            // Anillo inflado 8 por lado (como el resto de la app), esquina 10 para casar con el
            // radio del tile (6) + inflado.
            var ring = new Border
            {
                Width = CoverW + 16,
                Height = CoverH + 16,
                CornerRadius = new CornerRadius(10),
            };
            ring.Classes.Add("selectionRing");
            Canvas.SetLeft(ring, x - 8);
            Canvas.SetTop(ring, -8);
            GamesRailHost.Children.Add(ring);

            _coverRings.Add(ring);
        }
    }

    private readonly List<Border> _coverRings = new();

    // Construye la lista de navegables con sus rectangulos ABSOLUTOS (mismos numeros que el XAML). El
    // orden aqui no importa para el movimiento (es direccional por geometria), solo para el indice.
    private void BuildFocusItems()
    {
        _items.Add(new FocusItem(HeroRing, 240, 42, 1098, 553));      // 0 Hero
        _items.Add(new FocusItem(RightTopRing, 1357, 42, 483, 287));  // 1 Get ready! (Cyberpunk)
        _items.Add(new FocusItem(RightBotRing, 1357, 346, 483, 249)); // 2 Pre-order AC Valhalla
        _items.Add(new FocusItem(SearchRing, 240, 613, 185, 268));    // 3 Search
        _items.Add(new FocusItem(RedeemRing, 443, 613, 397, 132));    // 4 Redeem a code
        _items.Add(new FocusItem(DealsRing, 443, 763, 397, 118));     // 5 Deals
        _items.Add(new FocusItem(CardARing, 858, 613, 480, 268));     // 6 Destiny 2
        _items.Add(new FocusItem(CardBRing, 1357, 613, 483, 268));    // 7 Halo Infinite

        for (var i = 0; i < _coverRings.Count; i++)                   // 8.. caratulas Games
        {
            _items.Add(new FocusItem(_coverRings[i], 240 + i * CoverPitch, GamesRailTop, CoverW, CoverH));
        }
    }

    public void Move(GamepadButton button)
    {
        // Con la barra lateral abierta, el mando la controla a ella (no al contenido).
        if (_sidebarState != SidebarState.Closed)
        {
            MoveSidebar(button);
            return;
        }

        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.Left:
                // Izquierda desde el contenido: si hay un tile a la izquierda, mover; si ya no queda
                // nada (estamos en la columna mas a la izquierda), ABRIR la barra lateral.
                if (FindNeighbor(Direction.Left) is var left && left >= 0)
                {
                    _focus = left;
                    UpdateSelection();
                }
                else
                {
                    OpenSidebar();
                }

                return;
            case GamepadButton.Right:
                MoveFocus(Direction.Right);
                return;
            case GamepadButton.Up:
                MoveFocus(Direction.Up);
                return;
            case GamepadButton.Down:
                MoveFocus(Direction.Down);
                return;
            // LB/RB: carrusel del hero, SOLO con el foco sobre el hero (index 0). Con el foco en otro
            // tile, los bumpers no hacen nada. Da la vuelta de forma CONTINUA (wrap) en los extremos.
            case GamepadButton.LB when _focus == 0:
                HeroPrev();
                return;
            case GamepadButton.RB when _focus == 0:
                HeroNext();
                return;
            // A (activar el tile) se cableara cuando el contenido sea funcional.
        }
    }

    // ===== Navegacion de la doble barra lateral =====

    // Prepara las transiciones de entrada y de salida. Curva CubicEaseOut = arranca rapido y frena al
    // final (sensacion "smooth" de asentarse) para lo que entra; CubicEaseIn (arranca suave y acelera)
    // para lo que sale, que es como se percibe natural que algo "se retire".
    private void BuildSidebarTransitions()
    {
        _col0In = Fade(Col0InMs);
        _col1In = SlideFade(Col1InMs, new CubicEaseOut());
        _col1Out = Slide(Col1OutMs, new CubicEaseIn(), PanelOutDelayMs);
        _col1Hide = SlideFade(Col1OutMs, new CubicEaseIn());
        _hostOut = Fade(SidebarOutMs, new CubicEaseIn(), PanelOutDelayMs);

        // Una transicion por fila (llevan el retardo de la cascada dentro), en los dos sentidos.
        _labelIn = new Transitions[NavRows.Length];
        _labelOut = new Transitions[NavRows.Length];
        for (var i = 0; i < NavRows.Length; i++)
        {
            _labelIn[i] = LabelTransitions(i, entering: true);
            _labelOut[i] = LabelTransitions(NavRows.Length - 1 - i, entering: false); // al reves
        }

        Col0Layer.Transitions = _col0In;
        Col1Layer.Transitions = _col1In;

        // La barrita del activo se desliza de fila a fila (translateY animado).
        SidebarActiveBar.Transitions = new Transitions
        {
            new TransformOperationsTransition
            {
                Property = RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(BarSlideMs),
                Easing = new CubicEaseOut(),
            },
        };

        // El rail (columna de iconos) cambia de tono entre colapsado y expandido FUNDIENDO el color,
        // y sus adornos de estado colapsado (linea, sombra, barrita verde) se funden en vez de
        // aparecer/desaparecer de golpe.
        RailPanel.Transitions = new Transitions
        {
            new BrushTransition { Property = Shape.FillProperty, Duration = TimeSpan.FromMilliseconds(RailToneMs) },
        };
        foreach (var deco in new Control[] { RailDivider, RailShadow, RailActiveBar })
        {
            deco.Transitions = Fade(RailToneMs);
        }
    }

    private static Transitions Fade(int ms, Easing? easing = null, int delayMs = 0) => new()
    {
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(ms),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            Easing = easing ?? new LinearEasing(),
        },
    };

    private static Transitions Slide(int ms, Easing easing, int delayMs = 0) => new()
    {
        new TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(ms),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            Easing = easing,
        },
    };

    private static Transitions SlideFade(int ms, Easing easing) => new()
    {
        new TransformOperationsTransition
        {
            Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(ms), Easing = easing,
        },
        new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(ms) },
    };

    private static string TranslateX(double px) =>
        $"translateX({px.ToString(CultureInfo.InvariantCulture)}px)";

    // Fija una capa en su estado INICIAL (desplazada a la izquierda + transparente) SIN animar y, en el
    // siguiente ciclo, la lleva a su sitio CON transicion -> desliza+funde. Anular las transiciones al
    // fijar el inicio evita que ese salto se anime tambien (mismo patron que SetFilm del hero).
    private static void AnimateIn(Control layer, Transitions trans, double fromTx)
    {
        layer.Transitions = null;
        layer.Opacity = 0;
        layer.RenderTransform = TransformOperations.Parse(TranslateX(fromTx));
        layer.Transitions = trans;
        Dispatcher.UIThread.Post(() =>
        {
            layer.Opacity = 1;
            layer.RenderTransform = TransformOperations.Parse(TranslateX(0));
        }, DispatcherPriority.Background);
    }

    // Lo contrario: retira la capa deslizandola de vuelta (el estado actual ya es el "desde", asi que
    // basta con poner la transicion de salida y fijar el destino). No toca la opacidad: al cerrar, el
    // fundido lo hace la barra ENTERA de una pieza (SidebarHost), para que nada se quede rezagado.
    private static void SlideOut(Control layer, Transitions trans, double toTx)
    {
        layer.Transitions = trans;
        layer.RenderTransform = TransformOperations.Parse(TranslateX(toTx));
    }

    // Igual pero solo opacidad (para el atenuado y la capa de etiquetas, que no se deslizan).
    private static void FadeIn(Control c, Transitions trans)
    {
        c.Transitions = null;
        c.Opacity = 0;
        c.Transitions = trans;
        Dispatcher.UIThread.Post(() => c.Opacity = 1, DispatcherPriority.Background);
    }

    private static void FadeOut(Control c, Transitions trans)
    {
        c.Transitions = trans;
        c.Opacity = 0;
    }

    // Entrada EN CASCADA de las etiquetas: todas arrancan desplazadas hacia los iconos y
    // transparentes, y entran una detras de otra de ARRIBA a ABAJO (el retardo lo lleva cada etiqueta
    // en su transicion).
    private void AnimateLabelsIn()
    {
        for (var i = 0; i < _sidebarLabels.Count; i++)
        {
            var label = _sidebarLabels[i];
            label.Transitions = null;
            label.Opacity = 0;
            label.RenderTransform = TransformOperations.Parse(TranslateX(LabelSlideX));
            label.Transitions = _labelIn[i];
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var label in _sidebarLabels)
            {
                label.Opacity = 1;
                label.RenderTransform = TransformOperations.Parse(TranslateX(0));
            }
        }, DispatcherPriority.Background);
    }

    // Salida EN CASCADA INVERSA: la MISMA animacion al reves (se va primero la fila de abajo y la
    // cascada sube), volviendo cada etiqueta hacia los iconos de los que salio.
    private void AnimateLabelsOut()
    {
        for (var i = 0; i < _sidebarLabels.Count; i++)
        {
            var label = _sidebarLabels[i];
            label.Transitions = _labelOut[i];
            label.Opacity = 0;
            label.RenderTransform = TransformOperations.Parse(TranslateX(LabelSlideX));
        }
    }

    // Abre la barra: despliega la col. de etiquetas sobre el contenido (SIN atenuarlo, a peticion del
    // usuario) y muestra la 2a columna si la seccion enfocada la tiene. El foco arranca en la seccion
    // activa de la Tienda (Home). Se recuerda el tile del contenido en _focus para volver a el al
    // cerrar. La entrada se ANIMA: el panel funde y las etiquetas entran en cascada.
    private static readonly IBrush RailToneCollapsed = new SolidColorBrush(Color.Parse("#1D2627"));

    // Con la barra abierta el rail deja de ser opaco: pasa al MISMO tinte translucido que la columna de
    // etiquetas (medido en "tienda barra lateral": el rail y las etiquetas son un unico panel). Detras
    // del rail solo hay el degradado del fondo, que ya es suave, asi que ahi no hace falta desenfocar.
    private static readonly IBrush RailToneExpanded = new SolidColorBrush(Color.Parse("#C71E2929"));

    // ===== Fondo ACRILICO de la barra =====
    // La Store de Xbox no pone paneles opacos: son translucidos sobre el contenido DESENFOCADO (medido
    // en la captura: detras del panel no queda ni un detalle reconocible, solo manchas de color). Aqui
    // se hace igual pero SIN coste continuo: al abrir se saca UNA foto de la pantalla (sin la barra,
    // que aun no esta visible), se desenfoca UNA vez, y esa imagen fija se usa de fondo. Al cerrar se
    // suelta (son ~8 MB): todo bajo demanda y acotado.
    private const double BlurRadius = 90;
    private static readonly PixelSize CanvasPixels = new(1920, 1080);
    private static readonly Vector CanvasDpi = new(96, 96);
    private Bitmap? _backdrop;

    private void CaptureBackdrop()
    {
        ReleaseBackdrop();
        try
        {
            // 1) Foto de la pantalla tal cual esta ahora (la barra todavia no se ha mostrado).
            using var raw = new RenderTargetBitmap(CanvasPixels, CanvasDpi);
            raw.Render(this);

            // 2) Se desenfoca UNA vez horneandola en otra imagen, en vez de dejar el efecto vivo
            //    (un desenfoque a pantalla completa en cada fotograma costaria GPU sin necesidad:
            //    el contenido de debajo no se mueve mientras la barra esta abierta).
            var blurred = new RenderTargetBitmap(CanvasPixels, CanvasDpi);
            var host = new Image
            {
                Source = raw,
                Width = 1920,
                Height = 1080,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = BlurRadius },
            };
            host.Measure(new Size(1920, 1080));
            host.Arrange(new Rect(0, 0, 1920, 1080));
            blurred.Render(host);
            _backdrop = blurred;
        }
        catch
        {
            // Si el sistema no puede rasterizar (driver raro), la barra sigue funcionando: se queda
            // con el tinte translucido sin desenfoque en vez de romperse.
            _backdrop = null;
        }

        BackdropCol0.Source = _backdrop;
        BackdropCol1.Source = _backdrop;
    }

    private void ReleaseBackdrop()
    {
        BackdropCol0.Source = null;
        BackdropCol1.Source = null;
        _backdrop?.Dispose();
        _backdrop = null;
    }

    private void OpenSidebar()
    {
        // Si se estaba cerrando, cancela el remate del cierre (reabrir a medio cierre es valido).
        _closeTimer?.Stop();

        _sidebarState = SidebarState.Col0;
        _col0Focus = ActiveSectionRow;

        // Apaga el anillo del contenido y el rail "colapsado" (linea + sombra + barra verde en Home);
        // el panel gris del rail funde al tono neutro del estado expandido.
        foreach (var it in _items)
        {
            it.Ring.Classes.Set("selected", false);
        }

        RailPanel.Fill = RailToneExpanded;
        RailDivider.Opacity = 0;
        RailShadow.Opacity = 0;
        RailActiveBar.Opacity = 0;

        _subShown = false;       // la 2a columna arranca oculta; se anima al aparecer
        _lastSubs = null;
        Col1Layer.Opacity = 0;   // evita cualquier destello de col1 antes de que UpdateSidebar decida

        // Foto desenfocada del contenido ANTES de mostrar la barra (si no, se fotografiaria a si misma).
        CaptureBackdrop();

        // La capa entera vuelve a opaca sin animar (si se estaba cerrando, corta ese fundido en seco).
        SidebarHost.Transitions = null;
        SidebarHost.Opacity = 1;
        SidebarHost.IsVisible = true;

        // Animacion de entrada: el panel de etiquetas funde y las etiquetas entran EN CASCADA
        // deslizandose desde los iconos (que ya estaban ahi).
        FadeIn(Col0Layer, _col0In);
        AnimateLabelsIn();

        UpdateSidebar(animateBar: false);
    }

    // Cierra la barra y devuelve el foco al contenido (el tile en _focus). Es la MISMA animacion de
    // apertura AL REVES: primero se recogen las filas en cascada inversa (la de abajo primero,
    // subiendo) volviendo hacia los iconos, y solo DESPUES se funden los paneles y la 2a columna se
    // mete detras de las etiquetas; asi ningun panel desaparece antes que su contenido. La capa se
    // oculta al terminar. El estado pasa a Closed ya, para que el mando vuelva a controlar el
    // contenido inmediatamente.
    private void CloseSidebar()
    {
        _sidebarState = SidebarState.Closed;

        AnimateLabelsOut();
        FadeOut(SidebarHost, _hostOut); // el fundido de los paneles lleva su retardo dentro
        if (_subShown)
        {
            AnimateSubTilesOut();
            SlideOut(Col1Layer, _col1Out, Col1SlideX);
        }

        // La barrita del activo vuelve deslizandose a la SECCION ACTIVA (Home) mientras se funde: asi
        // acaba justo encima de la barrita del rail colapsado, en vez de verse dos barras a la vez.
        MoveActiveBar(ActiveSectionRow, animate: true);

        // El rail vuelve a su tono y adornos de estado colapsado, tambien fundiendo.
        RailPanel.Fill = RailToneCollapsed;
        RailDivider.Opacity = 1;
        RailShadow.Opacity = 1;
        RailActiveBar.Opacity = 1;

        UpdateSelection(); // el anillo del contenido vuelve a encenderse

        _closeTimer ??= CreateCloseTimer();
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    // Remata el cierre: cuando la salida ha terminado, oculta la capa entera (asi deja de pintarse).
    private DispatcherTimer CreateCloseTimer()
    {
        // Tiene que cubrir la salida ENTERA: la cascada inversa de las filas (la ultima arranca tras
        // NavRows.Length-1 escalones) y, despues, el fundido retardado de los paneles.
        var cascadeMs = (NavRows.Length - 1) * LabelOutStepMs + LabelOutMs;
        var panelsMs = PanelOutDelayMs + Math.Max(SidebarOutMs, Col1OutMs);
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(cascadeMs, panelsMs) + 40),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_sidebarState == SidebarState.Closed)
            {
                SidebarHost.IsVisible = false;
                ReleaseBackdrop(); // la barra ya no se ve: se sueltan los ~8 MB de la foto
            }
        };
        return timer;
    }

    private void MoveSidebar(GamepadButton button)
    {
        if (_sidebarState == SidebarState.Col0)
        {
            switch (button)
            {
                case GamepadButton.Up:
                    if (_col0Focus > 0)
                    {
                        _col0Focus--;
                        UpdateSidebar();
                    }

                    return;
                case GamepadButton.Down:
                    if (_col0Focus < NavRows.Length - 1)
                    {
                        _col0Focus++;
                        UpdateSidebar();
                    }

                    return;
                case GamepadButton.Right:
                case GamepadButton.A when !NavRows[_col0Focus].IsClose:
                    // Entrar a la 2a columna, solo si la categoria tiene subcategorias.
                    if (NavRows[_col0Focus].Subs.Length > 0)
                    {
                        _sidebarState = SidebarState.Col1;
                        _col1Focus = 0;
                        UpdateSidebar();
                    }

                    return;
                // Cerrar la barra: solo con B o con A sobre CLOSE. IZQUIERDA aqui NO hace nada (a
                // proposito): estando ya en la columna de etiquetas, volver a la izquierda
                // cerraba la barra y devolvia el foco al contenido, que no es lo que se busca.
                case GamepadButton.A when NavRows[_col0Focus].IsClose:
                case GamepadButton.B:
                    CloseSidebar();
                    return;
            }

            return;
        }

        // Col1 (subcategorias).
        switch (button)
        {
            case GamepadButton.Up:
                if (_col1Focus > 0)
                {
                    _col1Focus--;
                    UpdateSidebar();
                }

                return;
            case GamepadButton.Down:
                if (_col1Focus < NavRows[_col0Focus].Subs.Length - 1)
                {
                    _col1Focus++;
                    UpdateSidebar();
                }

                return;
            case GamepadButton.Left:
            case GamepadButton.B:
                _sidebarState = SidebarState.Col0;
                UpdateSidebar();
                return;
            case GamepadButton.A:
                // Abrir la subcategoria, si ya tiene pantalla. La barra se cierra primero (con su
                // animacion) para no dejarla puesta debajo de la pagina nueva.
                var sub = NavRows[_col0Focus].Subs[_col1Focus];
                if (HasPage(sub))
                {
                    CloseSidebar();
                    CategoryRequested?.Invoke(sub);
                }

                return;
        }
    }

    // Refresca la barra segun el estado: barra del activo en la fila enfocada de col0, colores de las
    // etiquetas (blanca la enfocada/activa, gris el resto) y la 2a columna (tiles de la categoria
    // enfocada + anillo en la subcategoria enfocada, solo cuando estamos dentro de col1).
    private void UpdateSidebar(bool animateBar = true)
    {
        // Barra del activo (color del tema): se DESLIZA hasta la fila enfocada (translateY animado).
        // Al abrir la barra se coloca de golpe (animateBar=false) para no verla viajar desde la fila 0.
        MoveActiveBar(_col0Focus, animateBar);

        // Etiquetas: blanca la fila enfocada, gris el resto (el cambio de color lo funde su transicion).
        for (var i = 0; i < _sidebarLabels.Count; i++)
        {
            _sidebarLabels[i].Foreground = i == _col0Focus ? Brushes.White : LabelIdle;
        }

        // 2a columna. Tres casos:
        //  - aparece (categoria con subcategorias viniendo de una sin ellas): emerge desde DETRAS de
        //    las etiquetas deslizandose a la derecha;
        //  - cambia de lista (de Games a Movies & TV, p.ej.): los tiles se refrescan con un
        //    deslizamiento corto para que se note el cambio sin re-desplegar toda la columna;
        //  - desaparece: se retira metiendose de nuevo detras de las etiquetas (no se corta de golpe;
        //    los tiles se quedan puestos hasta que la columna es invisible).
        var subs = NavRows[_col0Focus].Subs;
        var wantSub = subs.Length > 0;
        if (wantSub)
        {
            SubColumn.IsVisible = true;
            if (!_subShown)
            {
                BuildSubTiles(subs);
                AnimateIn(Col1Layer, _col1In, Col1SlideX);
            }
            else if (!ReferenceEquals(subs, _lastSubs))
            {
                BuildSubTiles(subs); // la cascada de los tiles nuevos ya la lanza BuildSubTiles
            }

            _lastSubs = subs;
        }
        else if (_subShown)
        {
            // Se retira metiendose detras de las etiquetas Y fundiendo (aqui la barra sigue abierta,
            // asi que la columna tiene que desaparecer por si misma).
            Col1Layer.Transitions = _col1Hide;
            Col1Layer.Opacity = 0;
            Col1Layer.RenderTransform = TransformOperations.Parse(TranslateX(Col1SlideX));
            _lastSubs = null;
        }

        _subShown = wantSub;

        // El anillo de foco solo se enciende cuando el foco esta DENTRO de col1.
        var showRing = _sidebarState == SidebarState.Col1;
        for (var i = 0; i < _subRings.Count; i++)
        {
            _subRings[i].Classes.Set("selected", showRing && i == _col1Focus);
        }
    }

    // Coloca la barrita del activo en una fila. La barra vive fijada en la fila 0 y se mueve con
    // translateY (una propiedad animable) para poder DESLIZARSE de fila a fila; con animate=false se
    // pone de golpe (al abrir la barra, para no verla viajar desde arriba).
    private void MoveActiveBar(int row, bool animate)
    {
        var trans = SidebarActiveBar.Transitions;
        if (!animate)
        {
            SidebarActiveBar.Transitions = null;
        }

        var dy = (NavRows[row].Center - NavRows[0].Center).ToString(CultureInfo.InvariantCulture);
        SidebarActiveBar.RenderTransform = TransformOperations.Parse($"translateY({dy}px)");

        if (!animate)
        {
            SidebarActiveBar.Transitions = trans;
        }
    }

    // (Re)construye los tiles de la 2a columna para una lista de subcategorias. Si esta vacia, la
    // columna queda sin tiles (categoria sin subcategorias, p.ej. Home/Search). Los tiles entran EN
    // CASCADA (uno detras de otro), igual que las etiquetas: se nota que la lista "se llena" en vez
    // de aparecer de golpe, tanto al desplegar la columna como al cambiar de categoria.
    private void BuildSubTiles(string[] subs)
    {
        SidebarSubHost.Children.Clear();
        _subRings.Clear();
        _subTiles.Clear();

        for (var i = 0; i < subs.Length; i++)
        {
            var top = SubRowTop0 + i * SubRowPitch - SubTileHeight / 2;

            var tile = new Border
            {
                Width = SubTileWidth,
                Height = SubTileHeight,
                Background = SubTileFill,
                CornerRadius = new CornerRadius(6),
                Transitions = SubTileTransitions(i),
                Effect = SubTileShadow(),
            };
            Canvas.SetLeft(tile, SubTileLeft);
            Canvas.SetTop(tile, top);
            SidebarSubHost.Children.Add(tile);
            _subTiles.Add(tile);

            tile.Child = new TextBlock
            {
                Text = subs[i],
                FontSize = 24,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(28, 0, 0, 0),
            };

            // Anillo de seleccion (sigue el color del tema, como el resto de la app), inflado 8. Se
            // mueve con su tile durante la cascada para no quedarse descolocado.
            var ring = new Border
            {
                Width = SubTileWidth + 16,
                Height = SubTileHeight + 16,
                CornerRadius = new CornerRadius(10),
                Transitions = SubTileTransitions(i),
            };
            ring.Classes.Add("selectionRing");
            Canvas.SetLeft(ring, SubTileLeft - 8);
            Canvas.SetTop(ring, top - 8);
            SidebarSubHost.Children.Add(ring);
            _subRings.Add(ring);
            _subTiles.Add(ring);
        }

        AnimateSubTilesIn();
    }

    // SOMBRA de los tiles de subcategoria (solo estos botones, nada mas de la app la lleva).
    // Medida pixel a pixel en "tienda barra lateral": justo DEBAJO del borde inferior de cada tile hay
    // una franja oscura de ~3 px que baja el color unos 13 puntos (~24% de negro sobre el panel) y una
    // cola tenue de ~10 px mas. En el borde izquierdo apenas hay 2-3 puntos y en el derecho nada ->
    // no es un contorno: es una sombra ARROJADA HACIA ABAJO. Llevado a nuestro lienzo (la captura es
    // 794 de alto, el nuestro 1080: factor 1,36) sale ~4 px de desplazamiento y ~12 de difuminado.
    private static DropShadowEffect SubTileShadow() => new()
    {
        OffsetX = 0,
        OffsetY = 3,
        BlurRadius = 10,
        Color = Colors.Black,
        Opacity = 0.3, // calibrado por captura: con 0,45 oscurecia un 58%, muy lejos del 24% medido
    };

    // Transiciones de un tile de subcategoria: desliza + funde con retardo segun su posicion en la
    // cascada (al entrar, de arriba abajo; al salir, al reves).
    private static Transitions SubTileTransitions(int step, bool entering = true) => new()
    {
        new TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(entering ? SubTileInMs : SubTileOutMs),
            Delay = TimeSpan.FromMilliseconds(step * SubTileStepMs),
            Easing = entering ? new CubicEaseOut() : new CubicEaseIn(),
        },
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(entering ? SubTileInMs : SubTileOutMs),
            Delay = TimeSpan.FromMilliseconds(step * SubTileStepMs),
        },
    };

    // Arranca la cascada de los tiles recien construidos (mismo patron que AnimateLabelsIn).
    private void AnimateSubTilesIn()
    {
        foreach (var t in _subTiles)
        {
            var trans = t.Transitions;
            t.Transitions = null;
            t.Opacity = 0;
            t.RenderTransform = TransformOperations.Parse(TranslateX(SubTileSlideX));
            t.Transitions = trans;
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var t in _subTiles)
            {
                t.Opacity = 1;
                t.RenderTransform = TransformOperations.Parse(TranslateX(0));
            }
        }, DispatcherPriority.Background);
    }

    // La cascada de los tiles AL REVES (al cerrar): se va primero el de abajo y va subiendo. Cada tile
    // lleva pegado su anillo en _subTiles (tile, anillo, tile, anillo...), de ahi el i/2 para la fila.
    private void AnimateSubTilesOut()
    {
        var rows = _subRings.Count;
        for (var i = 0; i < _subTiles.Count; i++)
        {
            var t = _subTiles[i];
            t.Transitions = SubTileTransitions(rows - 1 - (i / 2), entering: false);
            t.Opacity = 0;
            t.RenderTransform = TransformOperations.Parse(TranslateX(SubTileSlideX));
        }
    }

    // Construye el filmstrip con CLONES en los extremos: [clon(N-1), 0, 1, ..., N-1, clon(0)]. Asi la
    // pagina logica p queda en el indice de filmstrip p+1, y hay una pagina "de mas" a cada lado para
    // que el wrap se deslice de forma continua antes de saltar (sin animar) a la pagina real.
    private void BuildHeroPages()
    {
        var n = HeroPages.Length;
        HeroFilmstrip.Children.Add(BuildHeroPagePanel(HeroPages[n - 1])); // clon de la ultima (izq)
        foreach (var page in HeroPages)
        {
            HeroFilmstrip.Children.Add(BuildHeroPagePanel(page));
        }
        HeroFilmstrip.Children.Add(BuildHeroPagePanel(HeroPages[0]));     // clon de la primera (dcha)
    }

    // Un panel de pagina: color plano de placeholder + titulo + subtitulo (mas adelante ira el artwork).
    private static Panel BuildHeroPagePanel(HeroPage page)
    {
        var panel = new Panel { Width = HeroWidth, Height = 553 };
        panel.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.Parse(page.Color)) });
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(40, 0, 0, 66),
        };
        text.Children.Add(new TextBlock
        {
            Text = page.Title, Foreground = Brushes.White, FontSize = 44, FontWeight = FontWeight.SemiBold,
        });
        text.Children.Add(new TextBlock
        {
            Text = page.Subtitle, Foreground = new SolidColorBrush(Color.Parse("#E6E6E6")), FontSize = 22,
            Margin = new Thickness(0, 10, 0, 0),
        });
        panel.Children.Add(text);
        return panel;
    }

    // Coloca el filmstrip en un indice (0..N+1). animate=false = "salto" del wrap sin transicion (quita
    // y repone la transicion definida en el XAML para que el cambio sea instantaneo).
    private void SetFilm(int filmIndex, bool animate)
    {
        if (!animate)
        {
            HeroFilmstrip.Transitions = null;
        }

        var offset = (-filmIndex * HeroWidth).ToString(CultureInfo.InvariantCulture);
        HeroFilmstrip.RenderTransform = TransformOperations.Parse($"translateX({offset}px)");

        if (!animate)
        {
            HeroFilmstrip.Transitions = _heroTransitions;
        }
    }

    // RB: pagina siguiente (el contenido se desliza a la izquierda). En la ultima, wrap CONTINUO a la
    // primera (desliza al clon de la primera y luego salta a la real).
    private void HeroNext()
    {
        if (_heroWrapping)
        {
            return;
        }

        if (_heroPage < HeroPages.Length - 1)
        {
            _heroPage++;
            SetFilm(_heroPage + 1, animate: true);
        }
        else
        {
            SetFilm(HeroPages.Length + 1, animate: true); // clon de la primera (extremo derecho)
            _heroPage = 0;
            StartWrapSnap();
        }

        UpdateDots();
    }

    // LB: pagina anterior (el contenido se desliza a la derecha). En la primera, wrap CONTINUO a la
    // ultima (desliza al clon de la ultima y luego salta a la real).
    private void HeroPrev()
    {
        if (_heroWrapping)
        {
            return;
        }

        if (_heroPage > 0)
        {
            _heroPage--;
            SetFilm(_heroPage + 1, animate: true);
        }
        else
        {
            SetFilm(0, animate: true); // clon de la ultima (extremo izquierdo)
            _heroPage = HeroPages.Length - 1;
            StartWrapSnap();
        }

        UpdateDots();
    }

    // Arranca el "salto" invisible del wrap: bloquea pulsaciones y, tras la animacion, SetFilm sin animar.
    private void StartWrapSnap()
    {
        _heroWrapping = true;
        _heroWrapTimer!.Stop();
        _heroWrapTimer.Start();
    }

    // Marca el punto activo (blanco) y el resto (blanco translucido).
    private void UpdateDots()
    {
        for (var i = 0; i < _heroDots.Length; i++)
        {
            _heroDots[i].Fill = i == _heroPage ? DotActive : DotInactive;
        }
    }

    private enum Direction { Left, Right, Up, Down }

    private void MoveFocus(Direction dir)
    {
        var next = FindNeighbor(dir);
        if (next >= 0)
        {
            _focus = next;
            UpdateSelection();
        }
    }

    // Navegacion direccional por geometria (como el XYFocus de las consolas). El candidato tiene que
    // caer en la direccion pedida (su centro mas alla del BORDE del tile actual: asi un tile que esta
    // "al lado" y solo se solapa un poco no cuenta como "abajo"). Entre los candidatos se prefiere
    // SIEMPRE a los ALINEADOS -los que se solapan con el actual en el eje perpendicular, o sea que
    // estan en la misma columna/fila- por encima de los desalineados, sin importar la distancia. Esto
    // es lo que hace que, con tiles de tamaños muy distintos (el hero enorme frente a las tarjetas),
    // "arriba" desde la tarjeta inferior-derecha vaya a la superior-derecha (misma columna) y no salte
    // en diagonal al hero. Entre los alineados gana el mas cercano; si no hay ninguno, se usa una
    // puntuacion con penalizacion por el hueco lateral.
    private int FindNeighbor(Direction dir)
    {
        var cur = _items[_focus];
        var bestAligned = -1;
        var bestAlignedPrimary = double.MaxValue;
        var bestOther = -1;
        var bestOtherScore = double.MaxValue;

        for (var i = 0; i < _items.Count; i++)
        {
            if (i == _focus)
            {
                continue;
            }

            var it = _items[i];

            // ¿esta el candidato en la direccion pedida? Centro mas alla del borde del actual.
            var inDir = dir switch
            {
                Direction.Left => it.CenterX < cur.X,
                Direction.Right => it.CenterX > cur.Right,
                Direction.Up => it.CenterY < cur.Y,
                Direction.Down => it.CenterY > cur.Bottom,
                _ => false,
            };
            if (!inDir)
            {
                continue;
            }

            double primary; // distancia en el eje del movimiento (entre centros)
            double perpGap; // hueco entre bordes en el eje perpendicular (0 = alineados/solapados)
            if (dir is Direction.Left or Direction.Right)
            {
                primary = Math.Abs(it.CenterX - cur.CenterX);
                perpGap = Math.Max(0, Math.Max(cur.Y - it.Bottom, it.Y - cur.Bottom));
            }
            else
            {
                primary = Math.Abs(it.CenterY - cur.CenterY);
                perpGap = Math.Max(0, Math.Max(cur.X - it.Right, it.X - cur.Right));
            }

            if (perpGap <= 0)
            {
                // Alineado (misma columna/fila): gana el mas cercano en el eje del movimiento.
                if (primary < bestAlignedPrimary)
                {
                    bestAlignedPrimary = primary;
                    bestAligned = i;
                }
            }
            else
            {
                // Desalineado: solo se usa si no hay ninguno alineado. Penaliza el hueco lateral.
                var score = primary + perpGap * 2;
                if (score < bestOtherScore)
                {
                    bestOtherScore = score;
                    bestOther = i;
                }
            }
        }

        return bestAligned >= 0 ? bestAligned : bestOther;
    }

    private void UpdateSelection()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].Ring.Classes.Set("selected", i == _focus);
        }
    }
}
