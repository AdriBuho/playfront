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
/// Store screen (1:1 with the Xbox Store). It lives in its own view so it loads ON DEMAND: MainWindow
/// creates it on entry (EnterStore) and releases it on exit (ExitStore) - nothing is built before it
/// is opened.
///
/// Built in stages. Done: background, narrow rail, Store Home layout and content NAVIGATION
/// (directional focus with the accent ring). Pending: real tile artwork. Leaving the screen is
/// signalled by events (ExitRequested).
/// </summary>
public partial class StoreView : UserControl
{
    /// <summary>Closing the Store and returning Home was requested (B at the Store's top level).</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// A CATEGORY PAGE was requested from the sidebar's second column (A on a subcategory that already
    /// has a screen). Only "Music apps" exists so far.
    /// </summary>
    public event Action<string>? CategoryRequested;

    /// <summary>Subcategories that already have a screen built.</summary>
    private static bool HasPage(string sub) => sub is "Music apps" or "Launchers";

    // One navigable element: its selection ring (to toggle "selected") plus its rectangle in ABSOLUTE
    // 1920x1080 canvas coordinates, used by the directional search. The rectangle is the TILE's, not
    // the ring's - the ring is inflated 8 px all round.
    private sealed record FocusItem(Border Ring, double X, double Y, double W, double H)
    {
        public double CenterX => X + W / 2;
        public double CenterY => Y + H / 2;
        public double Right => X + W;
        public double Bottom => Y + H;
    }

    private readonly List<FocusItem> _items = new();
    private int _focus;

    // "Games" row: placeholder covers, LANDSCAPE (measured off the reference - they are not portrait).
    // Geometry is relative to GamesRailHost and converted to absolute for the directional search. They
    // start at the content edge, and the rail runs off the right and is clipped at the bottom, as in
    // the reference.
    private const int GamesCoverCount = 6;
    private const double GamesRailTop = 948;
    private const double CoverW = 306;
    private const double CoverH = 172;
    private const double CoverPitch = 325;

    // ===== HERO (spotlight) carousel =====
    // 5 placeholder pages, each a flat colour until real game artwork lands. LB/RB page through them,
    // but ONLY with focus on the hero (index 0), and the filmstrip slides towards the change.
    private sealed record HeroPage(string Title, string Subtitle, string Color);

    private static readonly HeroPage[] HeroPages =
    {
        new("Ori and the Will of the Wisps", "Embark on an all-new adventure", "#2E5E8C"),
        new("Halo Infinite", "Master Chief's greatest journey", "#3C7A3C"),
        new("Forza Horizon 5", "Your ultimate horizon adventure", "#8C3C7A"),
        new("Starfield", "Explore the stars", "#B5762A"),
        new("Sea of Thieves", "A shared-world pirate adventure", "#2A8C8C"),
    };

    private const double HeroWidth = 1098; // hero width (= one filmstrip page)
    private int _heroPage;                 // current logical page (0..N-1)
    private Ellipse[] _heroDots = null!;
    private static readonly IBrush DotActive = Brushes.White;
    private static readonly IBrush DotInactive = new SolidColorBrush(Color.Parse("#7AFFFFFF"));

    // INFINITE carousel via clones: the filmstrip carries a clone of the LAST page at the start and one
    // of the FIRST at the end -> [clone(N-1), 0, 1, ..., N-1, clone(0)]. Logical page p sits at
    // filmstrip index p+1. Going past either end slides onto the clone (continuous, no rewind) and,
    // once the animation ends, JUMPS without animation to the equivalent real page. That jump is
    // covered by _heroWrapping (input ignored during it) plus a timer.
    private Transitions? _heroTransitions; // the transition declared in the XAML, removed for the jump
    private bool _heroWrapping;
    private DispatcherTimer? _heroWrapTimer;

    // ===== DOUBLE SIDEBAR =====
    // Sidebar state: closed (navigating content), or open with focus in the LABEL column (col0) or the
    // SUBCATEGORY column (col1). It opens on Left from the content when there is nothing further left;
    // it closes with B.
    private enum SidebarState { Closed, Col0, Col1 }
    private SidebarState _sidebarState = SidebarState.Closed;
    private const int ActiveSectionRow = 2; // Home: the Store section currently being shown
    private int _col0Focus;  // focused row in the label column (index into NavRows)
    private int _col1Focus;  // focused subcategory in the second column

    // One row of the expanded rail: its TEXT, the row's vertical CENTRE (= icon centre, in 1920x1080
    // canvas coordinates) and its list of SUBCATEGORIES (empty = no second column).
    //
    // The sections are EXACTLY those in the reference capture: Search, Home, Games, Apps, Hardware |
    // Lists, Cart, Redeem | CLOSE. (Movies & TV and Settings do not exist in that Store version, and
    // what is elsewhere called "Wishlist" is "Lists" here.)
    //
    // ONLY subcategory lists seen OPEN in a reference capture are filled in - Home, Games and Apps.
    // Every other section is deliberately left without subcategories: there is no capture of them and
    // they are not invented.
    private sealed record NavRow(string Label, double Center, string[] Subs, bool IsClose = false);
    private static readonly string[] NoSub = Array.Empty<string>();
    private static readonly NavRow[] NavRows =
    {
        new("(profile)",     63,   NoSub),  // 0 profile: SidebarProfileName paints the Windows user name
        new("Search",        150,  NoSub),  // 1
        new("Home",          233,  new[] { "Store Home", "Deals", "Subscriptions" }), // 2
        // Games: the 11 entries VISIBLE in the capture. The original also shows a down arrow (more list
        // below) which is deliberately omitted: the list ends at "Most played" and there is no scroll.
        new("Games",         316,  new[]
        {
            "Games Home", "Add-ons", "Accessibility in games", "Subscriptions", "New games",
            "Coming soon", "Top paid", "Optimized for Series X|S", "Game demos", "Top free",
            "Most played",
        }), // 3
        // Two entries here are OURS, not the reference's. "Launchers" heads the list because it is
        // what this store is actually for on a PC - Steam, Epic and the rest - and the console has no
        // equivalent. "Music apps" is a shortened "Popular music apps".
        new("Apps",          399,  new[] { "Launchers", "Apps Home", "Entertainment apps", "Apps for gamers", "Apps with trials", "Popular apps", "Music apps" }), // 4
        new("Hardware",      482,  NoSub),  // 5
        new("Lists",         577,  NoSub),  // 6 (after the divider)
        new("Cart",          660,  NoSub),  // 7
        new("Redeem",        743,  NoSub),  // 8
        new("CLOSE",         1013, NoSub, IsClose: true), // 9
    };

    // SUBCATEGORY column geometry, measured off the reference and mapped onto the canvas: tiles
    // left-aligned in the panel, first row centred at 150 (the Search row) and 83 pitch, same as the
    // labels. Grey fill measured at (66,70,71).
    private const double SubTileLeft = 420;
    private const double SubTileWidth = 340;
    private const double SubTileHeight = 70;
    private const double SubRowTop0 = 150;   // centre of the first row
    private const double SubRowPitch = 83;
    private static readonly IBrush SubTileFill = new SolidColorBrush(Color.Parse("#424647"));
    private static readonly IBrush LabelIdle = new SolidColorBrush(Color.Parse("#B9BEC1"));

    private readonly List<TextBlock> _sidebarLabels = new();
    private readonly List<Border> _subRings = new();
    private readonly List<Border> _subTiles = new(); // second-column tiles + rings (for the cascade)

    // ===== Sidebar animation timings (ms) =====
    // The EXIT is deliberately shorter than the ENTRY: opening should feel like the bar unfolding,
    // closing like it getting out of the way and handing the content back.
    private const int Col0InMs = 170;
    private const int LabelInMs = 260, LabelStepMs = 18;  // each label enters 18 ms after the previous
    private const int LabelOutMs = 180, LabelOutStepMs = 16; // ...and leaves the same way, REVERSED
    private const int Col1InMs = 320, Col1OutMs = 190;
    private const int SubTileInMs = 240, SubTileStepMs = 14; // subcategory tile cascade
    private const int SubTileOutMs = 180;                    // ...and its reverse on close
    private const double SubTileSlideX = -34;                // where each tile slides in from
    private const int BarSlideMs = 200;  // the active bar sliding from row to row
    private const int RailToneMs = 220;  // rail tone crossfade, collapsed <-> expanded
    private const double LabelSlideX = -26; // where each label slides in from (towards the icons)
    private const double Col1SlideX = -170; // the second column emerges from behind the labels

    // On close the ROWS leave first (reverse cascade, bottom row first, working up) and only then do
    // the PANELS fade; otherwise a panel would vanish before its labels and they would be left floating
    // over the content for an instant.
    private const int PanelOutDelayMs = 190, SidebarOutMs = 160;

    // Entry and EXIT transitions (different durations). Kept so they can be nulled for an instant while
    // the starting state is set without animating - the same trick the hero carousel uses.
    private Transitions _col0In = null!;
    private Transitions _col1In = null!, _col1Out = null!, _col1Hide = null!;
    private Transitions _hostOut = null!;
    private Transitions[] _labelIn = null!, _labelOut = null!; // one per row, each carrying its delay
    private bool _subShown;          // whether the second column is currently out (animate only on appear)
    private string[]? _lastSubs;     // last list painted there, to avoid re-animating an unchanged one
    private DispatcherTimer? _closeTimer; // finishes the close (hides the layer) when the exit ends

    public StoreView()
    {
        InitializeComponent();

        _heroDots = new[] { HeroDot0, HeroDot1, HeroDot2, HeroDot3, HeroDot4 };
        BuildHeroPages();
        _heroTransitions = HeroFilmstrip.Transitions; // keep the transition declared in the XAML
        SetFilm(1, animate: false);                   // start on page 0 (filmstrip index 1, past the clone)
        UpdateDots();
        // Wrap-jump timer: after the animation (~300 ms), snap to the real page without animating.
        _heroWrapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _heroWrapTimer.Tick += (_, _) =>
        {
            _heroWrapTimer!.Stop();
            SetFilm(_heroPage + 1, animate: false);
            _heroWrapping = false;
        };

        // Profile name = the WINDOWS user. When Playfront gets its own or an Xbox account, change it
        // here.
        SidebarProfileName.Text = string.IsNullOrWhiteSpace(Environment.UserName) ? "Player" : Environment.UserName;

        BuildGamesRail();
        BuildFocusItems();
        BuildSidebarLabels();
        BuildSidebarTransitions();

        // Initial focus on the HERO, the natural top-left entry point. The capture shows SEARCH focused
        // because that is where it happened to be, not a fixed state.
        _focus = 0;
        UpdateSelection();
    }

    // Creates the col0 text labels, to the right of the rail icons. Built once; colour (grey/white) and
    // the active bar update as focus moves. Row 0's name is painted by SidebarProfileName in the XAML,
    // so that row gets no label here.
    private void BuildSidebarLabels()
    {
        for (var i = 0; i < NavRows.Length; i++)
        {
            if (i == 0)
            {
                _sidebarLabels.Add(SidebarProfileName); // the profile name already lives in the XAML
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

        // The active bar is positioned ONCE on row 0 and moves from there with translateY, an animatable
        // property, so it can SLIDE between rows instead of jumping.
        Canvas.SetTop(SidebarActiveBar, NavRows[0].Center - 14);
    }

    // A label's transitions. ENTRY: slides in from the icons and fades, delayed by its row -> cascade
    // from TOP to BOTTOM. EXIT: the same REVERSED (bottom row first, cascading up). Also cross-fades
    // grey<->white as the focused row changes.
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

    // Builds the Games row covers and their rings inside GamesRailHost. Grey placeholders for now; real
    // artwork later.
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

            // Ring inflated 8 per side (as everywhere else in the app); corner 10 to match the tile
            // radius (6) plus the inflation.
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

    // Builds the navigable list with ABSOLUTE rectangles (same numbers as the XAML). Order here does not
    // affect movement, which is geometric; it only fixes the indices.
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

        for (var i = 0; i < _coverRings.Count; i++)                   // 8.. Games covers
        {
            _items.Add(new FocusItem(_coverRings[i], 240 + i * CoverPitch, GamesRailTop, CoverW, CoverH));
        }
    }

    public void Move(GamepadButton button)
    {
        // With the sidebar open, the controller drives it, not the content.
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
                // Left from the content: move if there is a tile to the left; if there is nothing left
                // (already in the leftmost column), OPEN the sidebar.
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
            // LB/RB: hero carousel, ONLY with focus on the hero (index 0). On any other tile the
            // bumpers do nothing. Wraps CONTINUOUSLY at either end.
            case GamepadButton.LB when _focus == 0:
                HeroPrev();
                return;
            case GamepadButton.RB when _focus == 0:
                HeroNext();
                return;
            // A (activating a tile) gets wired up once the content is functional.
        }
    }

    // ===== Double sidebar navigation =====

    // Prepares the entry and exit transitions. CubicEaseOut - fast start, slowing to a settle - for
    // anything entering; CubicEaseIn - gentle start, accelerating - for anything leaving, which is how
    // something withdrawing reads as natural.
    private void BuildSidebarTransitions()
    {
        _col0In = Fade(Col0InMs);
        _col1In = SlideFade(Col1InMs, new CubicEaseOut());
        _col1Out = Slide(Col1OutMs, new CubicEaseIn(), PanelOutDelayMs);
        _col1Hide = SlideFade(Col1OutMs, new CubicEaseIn());
        _hostOut = Fade(SidebarOutMs, new CubicEaseIn(), PanelOutDelayMs);

        // One transition per row, in both directions, each carrying its cascade delay.
        _labelIn = new Transitions[NavRows.Length];
        _labelOut = new Transitions[NavRows.Length];
        for (var i = 0; i < NavRows.Length; i++)
        {
            _labelIn[i] = LabelTransitions(i, entering: true);
            _labelOut[i] = LabelTransitions(NavRows.Length - 1 - i, entering: false); // reversed
        }

        Col0Layer.Transitions = _col0In;
        Col1Layer.Transitions = _col1In;

        // The active bar slides between rows (animated translateY).
        SidebarActiveBar.Transitions = new Transitions
        {
            new TransformOperationsTransition
            {
                Property = RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(BarSlideMs),
                Easing = new CubicEaseOut(),
            },
        };

        // The rail (icon column) cross-fades its tone between collapsed and expanded, and its
        // collapsed-state decorations (line, shadow, accent bar) fade instead of popping.
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

    // Places a layer at its STARTING state (shifted left and transparent) WITHOUT animating and, on the
    // next cycle, moves it home WITH its transition -> slide plus fade. Nulling the transitions while
    // setting the start stops that jump animating too (same pattern as the hero's SetFilm).
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

    // The opposite: withdraws the layer by sliding it back (the current state is already the "from", so
    // setting the exit transition and the destination is enough). Opacity is untouched: on close the
    // fade is done by the WHOLE bar at once (SidebarHost), so nothing lags behind.
    private static void SlideOut(Control layer, Transitions trans, double toTx)
    {
        layer.Transitions = trans;
        layer.RenderTransform = TransformOperations.Parse(TranslateX(toTx));
    }

    // Same, opacity only (for the dim layer and the label layer, which do not slide).
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

    // CASCADING label entry: all start shifted towards the icons and transparent, then come in one
    // after another from TOP to BOTTOM (each label's delay lives in its own transition).
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

    // REVERSE CASCADE exit: the SAME animation backwards (bottom row leaves first, cascading up), each
    // label returning towards the icons it came from.
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

    // Opens the bar: unfolds the label column over the content (WITHOUT dimming it) and shows the second
    // column if the focused section has one. Focus starts on the Store's active section (Home). The
    // content tile stays in _focus so closing returns to it. The entry is animated: the panel fades and
    // the labels cascade in.
    private static readonly IBrush RailToneCollapsed = new SolidColorBrush(Color.Parse("#1D2627"));

    // With the bar open the rail stops being opaque and takes the SAME translucent tint as the label
    // column - measured: rail and labels are a single panel. Behind the rail there is only the
    // background gradient, already soft, so no blur is needed there.
    private static readonly IBrush RailToneExpanded = new SolidColorBrush(Color.Parse("#C71E2929"));

    // ===== Acrylic backdrop =====
    // The Xbox Store uses translucent panels over BLURRED content, not opaque ones (measured: behind
    // the panel not one detail survives, only colour blobs). Same here but with no continuous cost: on
    // open, ONE snapshot of the screen is taken (before the bar is visible), blurred ONCE, and that
    // still image is used as the background. On close it is released - it is ~8 MB.
    private const double BlurRadius = 90;
    private static readonly PixelSize CanvasPixels = new(1920, 1080);
    private static readonly Vector CanvasDpi = new(96, 96);
    private Bitmap? _backdrop;

    private void CaptureBackdrop()
    {
        ReleaseBackdrop();
        try
        {
            // 1) Snapshot of the screen as it is right now (the bar is not shown yet).
            using var raw = new RenderTargetBitmap(CanvasPixels, CanvasDpi);
            raw.Render(this);

            // 2) Blurred ONCE by baking it into another image rather than leaving the effect live: a
            //    full-screen blur every frame would burn GPU for nothing, since the content underneath
            //    does not move while the bar is open.
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
            // If the system cannot rasterize (odd driver), the bar still works: it keeps the
            // translucent tint without the blur instead of breaking.
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
        // If it was closing, cancel the close finisher - reopening mid-close is valid.
        _closeTimer?.Stop();

        _sidebarState = SidebarState.Col0;
        _col0Focus = ActiveSectionRow;

        // Turns off the content ring and the collapsed rail decorations (line, shadow, accent bar on
        // Home); the rail panel fades to the expanded state's neutral tone.
        foreach (var it in _items)
        {
            it.Ring.Classes.Set("selected", false);
        }

        RailPanel.Fill = RailToneExpanded;
        RailDivider.Opacity = 0;
        RailShadow.Opacity = 0;
        RailActiveBar.Opacity = 0;

        _subShown = false;       // the second column starts hidden and animates in when it appears
        _lastSubs = null;
        Col1Layer.Opacity = 0;   // avoids any flash of col1 before UpdateSidebar decides

        // Blurred snapshot of the content BEFORE showing the bar; otherwise it would photograph itself.
        CaptureBackdrop();

        // The whole layer goes opaque without animating; if it was closing, that cuts the fade dead.
        SidebarHost.Transitions = null;
        SidebarHost.Opacity = 1;
        SidebarHost.IsVisible = true;

        // Entry animation: the label panel fades in and the labels CASCADE in, sliding from the icons
        // that were already there.
        FadeIn(Col0Layer, _col0In);
        AnimateLabelsIn();

        UpdateSidebar(animateBar: false);
    }

    // Closes the bar and returns focus to the content (the tile in _focus). It is the opening animation
    // REVERSED: the rows retract first in reverse cascade (bottom first, working up) back towards the
    // icons, and only THEN do the panels fade and the second column tuck behind the labels - so no
    // panel disappears before its contents. The layer is hidden when it finishes. State goes to Closed
    // immediately so the controller drives the content again straight away.
    private void CloseSidebar()
    {
        _sidebarState = SidebarState.Closed;

        AnimateLabelsOut();
        FadeOut(SidebarHost, _hostOut); // the panel fade carries its own delay
        if (_subShown)
        {
            AnimateSubTilesOut();
            SlideOut(Col1Layer, _col1Out, Col1SlideX);
        }

        // The active bar slides back to the ACTIVE SECTION (Home) while fading, so it ends up right on
        // top of the collapsed rail's bar instead of two bars being visible at once.
        MoveActiveBar(ActiveSectionRow, animate: true);

        // The rail returns to its collapsed tone and decorations, also by fading.
        RailPanel.Fill = RailToneCollapsed;
        RailDivider.Opacity = 1;
        RailShadow.Opacity = 1;
        RailActiveBar.Opacity = 1;

        UpdateSelection(); // the content ring comes back on

        _closeTimer ??= CreateCloseTimer();
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    // Finishes the close: once the exit is done, hides the whole layer so it stops being painted.
    private DispatcherTimer CreateCloseTimer()
    {
        // Must cover the ENTIRE exit: the rows' reverse cascade (the last one starts after
        // NavRows.Length-1 steps) and, after that, the delayed panel fade.
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
                ReleaseBackdrop(); // the bar is no longer visible: release the snapshot's ~8 MB
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
                    // Enter the second column, only if the category has subcategories.
                    if (NavRows[_col0Focus].Subs.Length > 0)
                    {
                        _sidebarState = SidebarState.Col1;
                        _col1Focus = 0;
                        UpdateSidebar();
                    }

                    return;
                // Closing the bar: B, or A on CLOSE. LEFT deliberately does nothing here - from the
                // label column, going left again used to close the bar and hand focus back to the
                // content, which is not what is wanted.
                case GamepadButton.A when NavRows[_col0Focus].IsClose:
                case GamepadButton.B:
                    CloseSidebar();
                    return;
            }

            return;
        }

        // Col1 (subcategories).
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
                // Open the subcategory if it already has a screen. The bar closes first, with its
                // animation, so it is not left sitting under the new page.
                var sub = NavRows[_col0Focus].Subs[_col1Focus];
                if (HasPage(sub))
                {
                    CloseSidebar();
                    CategoryRequested?.Invoke(sub);
                }

                return;
        }
    }

    // Refreshes the bar for the current state: active bar on col0's focused row, label colours (white
    // for the focused one, grey for the rest) and the second column (tiles for the focused category
    // plus a ring on the focused subcategory, only while focus is inside col1).
    private void UpdateSidebar(bool animateBar = true)
    {
        // Active bar (theme colour): SLIDES to the focused row via animated translateY. On opening it is
        // placed instantly (animateBar=false) so it is not seen travelling from row 0.
        MoveActiveBar(_col0Focus, animateBar);

        // Labels: white for the focused row, grey for the rest; the colour change is faded by its own
        // transition.
        for (var i = 0; i < _sidebarLabels.Count; i++)
        {
            _sidebarLabels[i].Foreground = i == _col0Focus ? Brushes.White : LabelIdle;
        }

        // Second column. Three cases:
        //  - it appears (a category with subcategories, coming from one without): emerges from BEHIND
        //    the labels, sliding right;
        //  - the list changes (Games to Apps, say): tiles refresh with a short slide so the change
        //    registers without re-unfolding the whole column;
        //  - it disappears: withdraws back behind the labels (not cut off - the tiles stay put until
        //    the column is invisible).
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
                BuildSubTiles(subs); // BuildSubTiles already kicks off the new tiles' cascade
            }

            _lastSubs = subs;
        }
        else if (_subShown)
        {
            // Withdraws behind the labels AND fades: the bar itself stays open here, so the column has
            // to disappear on its own.
            Col1Layer.Transitions = _col1Hide;
            Col1Layer.Opacity = 0;
            Col1Layer.RenderTransform = TransformOperations.Parse(TranslateX(Col1SlideX));
            _lastSubs = null;
        }

        _subShown = wantSub;

        // The focus ring only lights up while focus is INSIDE col1.
        var showRing = _sidebarState == SidebarState.Col1;
        for (var i = 0; i < _subRings.Count; i++)
        {
            _subRings[i].Classes.Set("selected", showRing && i == _col1Focus);
        }
    }

    // Places the active bar on a row. It is pinned to row 0 and moved with translateY, an animatable
    // property, so it can SLIDE between rows; animate=false places it instantly (used when opening, so
    // it is not seen travelling down from the top).
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

    // (Re)builds the second column's tiles for a list of subcategories. An empty list leaves the column
    // without tiles. The tiles CASCADE in one after another, like the labels, so the list reads as
    // filling up rather than popping - both when the column unfolds and when the category changes.
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

            // Selection ring (follows the theme colour, like the rest of the app), inflated 8. It moves
            // with its tile during the cascade so it never ends up offset.
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

    // SHADOW on the subcategory tiles - the only buttons in the app that have one.
    // Measured pixel by pixel: just BELOW each tile's bottom edge there is a ~3 px dark band that drops
    // the colour about 13 levels (~24% black over the panel) plus a faint ~10 px tail. The left edge
    // shows barely 2-3 levels and the right none, so it is not an outline: it is a shadow CAST
    // DOWNWARDS. Scaled to this canvas (the capture is 794 tall against 1080, factor 1.36) that comes
    // to ~4 px offset and ~12 blur.
    private static DropShadowEffect SubTileShadow() => new()
    {
        OffsetX = 0,
        OffsetY = 3,
        BlurRadius = 10,
        Color = Colors.Black,
        Opacity = 0.3, // calibrated against the capture: 0.45 darkened 58%, far off the measured 24%
    };

    // A subcategory tile's transitions: slide plus fade, delayed by its position in the cascade (top
    // down on entry, reversed on exit).
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

    // Kicks off the cascade for the tiles just built (same pattern as AnimateLabelsIn).
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

    // The tile cascade REVERSED (on close): the bottom one leaves first, working up. _subTiles
    // interleaves each tile with its ring (tile, ring, tile, ring...), hence the i/2 for the row.
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

    // Builds the filmstrip with CLONES at the ends: [clone(N-1), 0, 1, ..., N-1, clone(0)]. Logical
    // page p ends up at filmstrip index p+1, and there is one spare page on each side so the wrap can
    // slide continuously before snapping (unanimated) to the real page.
    private void BuildHeroPages()
    {
        var n = HeroPages.Length;
        HeroFilmstrip.Children.Add(BuildHeroPagePanel(HeroPages[n - 1])); // clone of the last (left)
        foreach (var page in HeroPages)
        {
            HeroFilmstrip.Children.Add(BuildHeroPagePanel(page));
        }
        HeroFilmstrip.Children.Add(BuildHeroPagePanel(HeroPages[0]));     // clone of the first (right)
    }

    // One page panel: flat placeholder colour plus title and subtitle (artwork comes later).
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

    // Positions the filmstrip at an index (0..N+1). animate=false is the wrap snap: it removes and
    // restores the XAML transition so the change is instant.
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

    // RB: next page (content slides left). On the last one it wraps CONTINUOUSLY to the first: slide
    // onto the first's clone, then snap to the real one.
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
            SetFilm(HeroPages.Length + 1, animate: true); // clone of the first (far right)
            _heroPage = 0;
            StartWrapSnap();
        }

        UpdateDots();
    }

    // LB: previous page (content slides right). On the first one it wraps CONTINUOUSLY to the last:
    // slide onto the last's clone, then snap to the real one.
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
            SetFilm(0, animate: true); // clone of the last (far left)
            _heroPage = HeroPages.Length - 1;
            StartWrapSnap();
        }

        UpdateDots();
    }

    // Starts the wrap's invisible snap: blocks input and, after the animation, SetFilm without animating.
    private void StartWrapSnap()
    {
        _heroWrapping = true;
        _heroWrapTimer!.Stop();
        _heroWrapTimer.Start();
    }

    // Marks the active dot white and the rest translucent white.
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

    // Geometric directional navigation, like the consoles' XYFocus. A candidate has to lie in the
    // requested direction - its centre past the current tile's EDGE, so a tile that merely sits
    // alongside and overlaps slightly does not count as "below". ALIGNED candidates (those overlapping
    // the current tile on the perpendicular axis, i.e. in the same row/column) ALWAYS beat unaligned
    // ones, regardless of distance. That is what makes "up" from the bottom-right card go to the
    // top-right card rather than cutting diagonally to the hero, despite the wildly different tile
    // sizes. Among aligned candidates the nearest wins; with none, a score penalising the lateral gap
    // is used.
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

            // Is the candidate in the requested direction? Centre past the current tile's edge.
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

            double primary; // distance along the movement axis (centre to centre)
            double perpGap; // edge gap on the perpendicular axis (0 = aligned/overlapping)
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
                // Aligned (same row/column): nearest along the movement axis wins.
                if (primary < bestAlignedPrimary)
                {
                    bestAlignedPrimary = primary;
                    bestAligned = i;
                }
            }
            else
            {
                // Unaligned: only used when nothing is aligned. The lateral gap is penalised.
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
