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

    // Full (100%) width of the battery fill bar, in the icon design canvas units (see the
    // "BatteryFill" Rectangle in the XAML) - must match that Rectangle's Width. It deliberately
    // tucks 1 unit under the outline rather than ending exactly at its inner edge, so no gaps show
    // between fill and outline on screens with a different scale/DPI factor.
    private const double BatteryFillMaxWidth = 20;
    // The battery bar colour no longer depends on state: it is ALWAYS green. The green is fixed
    // (BatteryFill.Fill in the XAML), so no colour is computed here any more - it used to be plain
    // white, orange below 20% and green while charging.

    // Minimum time the Settings loading screen (gear over black) stays visible, even if the real
    // screen is ready sooner. Without this floor, when Settings loads instantly the loader would
    // flash for one frame and read as a glitch instead of a transition.
    private const int MinSettingsLoadingMilliseconds = 300;

    // Must match the Duration of SettingsLoadingScreen's Opacity DoubleTransition in the XAML - it
    // is used to wait for the fade-out to finish before hiding the Grid entirely (otherwise it
    // would vanish abruptly halfway through the fade).
    private static readonly TimeSpan SettingsLoadingFadeDuration = TimeSpan.FromMilliseconds(300);

    private readonly Border[][] _rows;

    // Selection ring of each home tile (the same Border.selectionRing used by Settings and
    // Personalization). Same indices as _rows: _homeRings[r][c] surrounds _rows[r][c].
    // Row 0 (navigation) has none - it uses its own lit-circle style.
    private readonly Border[][] _homeRings;
    private readonly double[][] _rowCenters;

    // Accent selection ring of each card/tile (see Border.selectionRing in the XAML). It lives in a
    // separate overlaid element and not on the card's own border because it is drawn OUTSIDE it, a
    // few pixels away. Same indices as _personalizationTiles: each ring lights up with the card it
    // surrounds.
    // JAGGED, indexed [column][row]: Personalization is three columns of 3 + 3 + 1, so the last one
    // declares a single row and the navigation just reads each column's length - no special case
    // for the short column.
    private readonly Border[][] _personalizationTiles;
    private readonly Border[][] _personalizationRings;

    // Cards and rings of "My color & theme" (My color / System theme).
    private readonly Border[] _colorThemeCards;
    private readonly Border[] _colorThemeRings;
    private readonly GamepadPoller _gamepad = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly BatteryMonitor _battery = new();
    private HardwareVideoBackgroundControl? _videoBackground;

    private int _row;
    private int _col;

    // Settings screen state (see Border.settingsNavItem/settingsCard in the XAML) - independent of
    // the home's _row/_col, so that going back leaves the home where it was.
    private bool _inSettings;

    // True while the black veil for entering/leaving Settings is on screen, so that a mistimed A/B
    // press does not fire a second transition on top of the one already running.
    private bool _settingsTransitioning;

    // The Settings view is created ON DEMAND on entry (EnterSettings) and released on exit
    // (ExitSettings); it is null when not in Settings. Its navigation and state live inside it.
    private SettingsView? _settingsView;

    // "System Updates" screen (Settings > System > Updates): hangs off Settings, built on demand
    // when opened and released on returning with B. While it is up, it owns the gamepad.
    private bool _inUpdates;
    private SystemUpdatesView? _updatesView;

    private bool _inConsoleInfo;
    private SystemConsoleInfoView? _consoleInfoView;

    private bool _inStorage;
    private SystemStorageView? _storageView;

    private bool _inLanguage;
    private SystemLanguageView? _languageView;

    private bool _inTime;
    private SystemTimeView? _timeView;

    // STORE state (opaque full screen covering the home, like Settings). The view is created ON
    // DEMAND on entry (EnterStore) and released on exit (ExitStore). While it is open the gamepad
    // navigates it (see Move) and the home video is paused (see IsHomeCovered).
    private bool _inStore;
    private StoreView? _storeView;

    // LIBRARY state ("My games & apps"): opaque full screen covering the home (like
    // Settings/Store). The view is created ON DEMAND on entry (EnterLibrary) and released on exit
    // (ExitLibrary). Visual only for now: inside it the gamepad only goes back with B.
    private bool _inLibrary;
    private LibraryView? _libraryView;

    // "General Personalization" state (see PersonalizationScreen in the XAML). It is a full screen
    // covering Settings, not a panel inside it, so it carries its own separate state: while it is
    // open the gamepad navigates it and not the card grid behind. Closing it with B leaves Settings
    // exactly as it was.
    private bool _inPersonalization;
    private int _pzCol;
    private int _pzRow;

    // "My color & theme" screen state (hangs off Personalization, see ColorThemeScreen in the XAML).
    // Since it sits above Personalization, it is checked before it in Move().
    private bool _inColorTheme;
    private int _colorThemeIndex;

    // Colour picker state (hangs off "My color & theme", see ColorPickerScreen). Indices 0..13 = the
    // 14 swatches (0..6 row 1, 7..13 row 2), 14 = OK button.
    private bool _inColorPicker;
    private int _colorPickerIndex;
    private readonly Border[] _colorSwatchRings = new Border[14];

    // "Applied" mark (white triangle + check) placed over the swatch whose colour is the current
    // accent. A single instance, reused.
    private Canvas? _appliedCheck;

    // Hex of the currently applied theme accent (the selection colour). Loaded from disk at startup
    // and changed when picking a colour. AccentTheme already applied it to the resources before the
    // window was created; it is kept here only to know which swatch to highlight when opening the
    // picker.
    private string _currentAccentHex = AccentTheme.DefaultHex;

    // The picker's 14 colours, sampled PIXEL BY PIXEL from the centre of each swatch in the
    // reference capture (its exact colours). Row 1 then row 2.
    private static readonly string[] ColorSwatchHexes =
    {
        // Ordered LIGHTEST to DARKEST (white top left); SAME order as AccentTheme.Palette.
        "#FFFFFF", "#DB5985", "#5AA029", "#D84F1F", "#A64AB3", "#207EBB", "#7552A1",
        "#23807F", "#2073C7", "#217F72", "#D01F2F", "#B21F75", "#207A1F", "#991F30",
    };

    // "My background" screen state (hangs off Personalization, see MyBackgroundScreen in the XAML).
    // Since it sits above Personalization, it is checked before it in Move(). Two columns now,
    // navigated as (column, row) like Personalization: left = the 3 background sources, right =
    // "Show selected game art" and "Restore default background".
    private bool _inMyBackground;
    private int _mbCol;
    private int _mbRow;
    // Which LEFT-HAND source is the ACTIVE background (carries the check triangle). Default 2 =
    // "Dynamic backgrounds", which is what the app shows right now.
    private int _myBackgroundActiveIndex = 2;
    private readonly Border[][] _myBackgroundTiles;
    private readonly Border[][] _myBackgroundRings;
    // "Active background" mark: white triangle + check in the top right corner of the active tile.
    // A single instance, reused (same idea as the colour picker's _appliedCheck).
    private Canvas? _myBackgroundCheck;

    // Geometry of the My background tiles (= Personalization's). Used both by the XAML and to place
    // the check triangle.
    private const double MbTileLeft = 112;
    private const double MbTileWidth = 438;
    private const double MbTileTop0 = 264;
    private const double MbTilePitch = 114;

    // "Solid colors" picker state (hangs off My background, see SolidColorsScreen in the XAML).
    // Indices 0..13 = the 14 grid slots (0..6 row 1, 7..13 row 2), 14 = OK button. Slot 0 is the
    // CUSTOM COLOUR tile (hue wheel): navigable but does nothing yet, so the 13 real colours live in
    // slots 1..13 - see SolidHexAtSlot.
    private bool _inSolidColors;
    private int _solidColorsIndex;
    private readonly Border[] _solidSwatchRings = new Border[14];
    // "Applied" mark (white triangle + check) over the swatch whose colour is the current background.
    private Canvas? _solidAppliedCheck;

    // Home background: null = dynamic video (the default); a hex = that solid colour. Loaded from
    // disk at startup (BackgroundSettings) and changed by picking a colour or pressing "Restore
    // default background".
    private string? _backgroundSolidHex;

    // The specific dynamic video chosen in "Dynamic backgrounds" (path relative to
    // Assets/Backgrounds), or null = the default background (the first in the library, see
    // DefaultBackground). Only applies when NO solid colour is active.
    private string? _backgroundVideoRelPath;

    // Geometry of the "Solid colors" grid, measured 1:1 on the reference (see the XAML). Used to
    // place the swatches, their rings and the applied mark.
    private const double SolidSwatchW = 246;
    private const double SolidSwatchH = 212;
    private const double SolidColX0 = 100;
    private const double SolidColPitch = 260;
    private const double SolidRow0Y = 306;
    private const double SolidRow1Y = 530;
    // 7x2 grid: the custom colour tile plus the 13 colours.
    private const int SolidSlotCount = 14;

    // "Solid colors - Custom" state (hangs off Solid colors, see CustomColorScreen in the XAML).
    // VISUAL ONLY for now: the six items take the ring but A does nothing and the sliders do not
    // move. Indices 0..2 = the three sliders, 3 = the hex card, 4 = SAVE, 5 = MATCH MY GAMERPIC.
    private bool _inCustomColor;
    private int _customColorIndex;
    private Border[]? _customColorCards;
    private Border[]? _customColorRings;

    // "General - Home" state (hangs off Personalization, see PersonalizationHomeScreen in the XAML).
    // VISUAL ONLY: the five controls take the ring but A does nothing. Indices 0..2 = the controls of
    // the first three columns, 3 = Edit groups, 4 = Edit games (both in the fourth column).
    private bool _inPersonalizationHome;
    private int _phIndex;
    private Border[]? _phControls;
    private Border[]? _phRings;

    // How far the strip slides left once focus reaches the fourth column, so that column ends up with
    // the same 112 margin the first one has instead of the 8 it would otherwise get. Measured on both
    // states of the reference: columns at 112/566/1020/1474 against 8/462/916/1370.
    private const double PhStripShift = 104;

    // Tile-count dropdown (the "9 tiles" control). Open state, which option the ring is on, and the
    // value that was showing when it opened so B can put it back.
    private bool _phTilesOpen;
    private int _phTilesOption;
    private string _phTilesPrevValue = "9 tiles";
    private Border[]? _phTilesRows;
    private Border[]? _phTilesRings;

    private static readonly string[] PhTilesOptions =
    {
        "4 tiles", "5 tiles", "6 tiles", "7 tiles", "8 tiles", "9 tiles",
    };

    // Rows are 56 tall (the control card is 98 - the panel does not reuse its height), and 842 is the
    // panel top that would put the FIRST option in the slot the card occupied. Both measured.
    private const double PhTilesRowHeight = 56;
    private const double PhTilesSlotTop = 842;

    // "Custom image" state (hangs off My background, see CustomImageScreen in the XAML). VISUAL ONLY:
    // the four source cards take the ring but A does nothing.
    private bool _inCustomImage;
    private int _customImageIndex;
    private Border[]? _customImageCards;
    private Border[]? _customImageRings;

    // "Dynamic backgrounds" state (hangs off My background). For now only the navigable STRUCTURE
    // with placeholder thumbnails; the real backgrounds come later. _dynFocus: 0 = tab row, 1 =
    // thumbnail row. _dynTab: 0 Games, 1 Xbox, 2 Abstract. _dynIndex: selected thumbnail within the
    // tab.
    private bool _inDynamic;
    private int _dynFocus;
    private int _dynTab;
    private int _dynIndex;
    private TextBlock[] _dynTabs = null!;

    // Full-screen preview of the FOCUSED background (even if not applied): a single video decoding
    // at a time, following the selected thumbnail. _dynPreviewTargetVideo = the one wanted (applied
    // after a short delay so scrubbing quickly through thumbnails does not spin up a decoder per
    // thumbnail). The poster (still image) shows instantly while the video starts.
    // "" = not resolved yet (forces the first pass).
    private HardwareVideoBackgroundControl? _dynPreviewVideo;
    private string? _dynPreviewTargetVideo = "";
    private DispatcherTimer? _dynPreviewTimer;

    // Thumbnail count per tab. Games is genuinely populated (31 real backgrounds in DynLibrary, no
    // placeholders). Xbox (35) and Abstract (129) are still PLACEHOLDERS (the real counts of the
    // video library still to be imported): their thumbnails render grey until processed.
    // When populating a tab, put the real background count here (= DynLibrary[tab].Length).
    private static readonly int[] DynTabCounts = { 31, 35, 129 };
    private const double DynThumbPitch = 274;   // thumbnail width (262) + gap (12)
    private const double DynRailSelX = 131;     // fixed x of the selected thumbnail (measured from the frame)

    // A REAL dynamic background: its display name + the video path and the poster path (the
    // thumbnail's still image), both relative to Assets/Backgrounds. They are added one by one;
    // positions without an entry stay as a grey placeholder.
    private sealed record DynBackground(string Name, string VideoRelPath, string PosterRelPath);

    // Library of real dynamic backgrounds per tab (0 Games, 1 Xbox, 2 Abstract), in order. Only
    // Games is populated so far; the rest come later.
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

    // Cache of the home's FULL RESOLUTION poster (~8 MB). Filled when APPLYING a background. Only
    // ONE is kept: that of the background currently set. On switching background the previous one is
    // released (and freed from the GPU), but with DEFERRED DISPOSAL (see ScheduleDispose): the image
    // just removed from screen may still be "in flight" on the GPU for a frame, so it is freed a
    // moment later, not immediately, to avoid a crash.
    private readonly global::System.Collections.Generic.Dictionary<string, Avalonia.Media.Imaging.Bitmap> _dynPosterCache = new();

    // The real background at (tab, index), or null if that thumbnail is still a placeholder.
    private static DynBackground? DynEntry(int tab, int index)
        => index >= 0 && index < DynLibrary[tab].Length ? DynLibrary[tab][index] : null;

    // Full on-disk path of a background asset from its relative path. The heavy assets may sit next
    // to the executable (development) or in the machine's shared folder (where the installer puts
    // them): AssetPaths decides, see AssetPaths.cs for why they live apart.
    private static string BackgroundFullPath(string relPath) => AssetPaths.Background(relPath);

    // The default background (first run and "Restore default background"): the FIRST in the library.
    // It used to be a placeholder video (dynamic-background.mp4) that no longer exists; now it is
    // the first real background. null if the library is empty.
    private static DynBackground? DefaultBackground()
    {
        foreach (var tab in DynLibrary)
        {
            foreach (var e in tab)
            {
                return e; // whichever comes first
            }
        }

        return null;
    }

    // Finds a library background by its video path (null if absent).
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

    // The video the home should play: the chosen one (if any) or, by default, the first in the
    // library. null if there is none (empty library).
    private string? ResolveHomeVideoPath()
    {
        var rel = _backgroundVideoRelPath ?? DefaultBackground()?.VideoRelPath;
        return rel is null ? null : BackgroundFullPath(rel);
    }

    // The underline width is no longer fixed: it is measured from the word's real width at runtime
    // (see UpdateTabUnderline).

    // Second action of the hint bar, per tab (in the reference frame: Games -> "See game details",
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

        // X centres computed from the XAML's own fixed coordinates (Canvas.Left + Width/2) rather
        // than reading Bounds, which is not ready until the first layout pass. Used for up/down
        // navigation (NearestColumn picks the closest column when changing row). These are the
        // AT REST centres (when arriving at a row from another, the tiles are still spread out, not
        // squeezed). Games row: 110 + i*195 + 154/2. Bottom row: left + 400/2. Row 0 (nav) is the
        // circles.
        _rowCenters = new[]
        {
            new double[] { 813.5, 885.5, 958.5, 1030.5, 1102.5 },
            new double[] { 187, 382, 577, 772, 967, 1162, 1357, 1552, 1747 },
            new double[] { 310, 748, 1186, 1624 },
        };

        _personalizationTiles = new[]
        {
            new[] { PzTile0, PzTile1, PzTile2 },   // Home / Guide / Games & apps
            new[] { PzTile3, PzTile4, PzTile5 },   // My profile / My color & theme / My background
            new[] { PzTile6 },                     // My home XBOX
        };
        _personalizationRings = new[]
        {
            new[] { PzRing0, PzRing1, PzRing2 },
            new[] { PzRing3, PzRing4, PzRing5 },
            new[] { PzRing6 },
        };

        // Five, not two: the last three only exist while the theme is "Scheduled" (see
        // ColorThemeCount), but they live in the same list so the navigation is one index.
        _colorThemeCards = new[] { CtCard0, CtCard1, CtSched0, CtSched1, CtSched2 };
        _colorThemeRings = new[] { CtRing0, CtRing1, CtSchedRing0, CtSchedRing1, CtSchedRing2 };

        _myBackgroundTiles = new[]
        {
            new[] { MbTile0, MbTile1, MbTile2 },   // Solid colors / Custom image / Dynamic backgrounds
            new[] { MbGameArt, MbRestore },        // Show selected game art / Restore default background
        };
        _myBackgroundRings = new[]
        {
            new[] { MbRing0, MbRing1, MbRing2 },
            new[] { MbGameArtRing, MbRestoreRing },
        };
        BuildMyBackgroundCheck();

        BuildColorSwatches();
        BuildSolidColorSwatches();
        _dynTabs = new[] { DynTabGames, DynTabXbox, DynTabAbstract };

        // App.OnFrameworkInitializationCompleted already applied the accent theme to the resources;
        // here we only sync the local state and the name shown on the "My color" card.
        _currentAccentHex = AccentTheme.LoadSavedHex();
        CtColorValue.Text = AccentTheme.NameFor(_currentAccentHex);

        // Starts with the FIRST GAME selected (games row, column 0), on purpose. A stock Xbox starts
        // on the navigation row ("My games & apps"), but landing straight on the games is preferred
        // here.
        _row = 1;
        _col = 0;
        UpdateSelection();
        UserNameText.Text = global::System.Environment.UserName;

        _gamepad.ButtonPressed += OnGamepadButtonPressed;
        // B HELD: only used to EXIT the YouTube screen (it does nothing elsewhere in the app; a tap
        // of B already acts as "back" on every screen).
        _gamepad.BHeld += () => CrashLog.Guard(() => { if (_inYouTube) ExitYouTube(); }, "bheld");
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        // Guard: gamepad polling runs here and ALL navigation hangs off it (Poll -> ButtonPressed ->
        // Move -> each screen's handlers), so a failure in any handler would surface here. Catching
        // it keeps the shell from going down.
        _pollTimer.Tick += (_, _) => CrashLog.Guard(_gamepad.Poll, "poll");
        _pollTimer.Start();

        // Settle delay before starting the focused background's preview video: scrubbing the
        // selection quickly across thumbnails must not spin up a decoder per thumbnail, only once
        // focus rests (~250 ms).
        _dynPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _dynPreviewTimer.Tick += (_, _) => CrashLog.Guard(() =>
        {
            _dynPreviewTimer!.Stop();
            if (_dynPreviewTargetVideo == null)
            {
                return; // placeholder thumbnail: no video to play
            }

            // The loading image (shown BLURRED) stays as the THUMBNAIL that UpdateDynPreview already
            // set: under the blur it is indistinguishable from a larger version, so no big poster is
            // decoded separately or held in RAM. Only the video source changes.

            // A SINGLE player: created the first time and thereafter only re-sourced (not
            // destroyed/recreated on every change, which is what used to stall the page). It is
            // revealed once its first frame is ready (OnDynPreviewReady), not before.
            EnsureDynPreviewControl();
            _dynPreviewVideo!.SetVideoSource(_dynPreviewTargetVideo);
        }, "dyn-preview-tick");

        KeyDown += (s, e) => CrashLog.Guard(() => OnKeyDown(s, e), "keydown");
        Opened += (_, _) => CrashLog.Guard(() => { CoverEntireMonitor(); UpdateHomeVideoState(); }, "opened");
        // The background video only decodes while the home is genuinely on screen. On losing focus (a
        // game or another window comes to the front) it is unloaded; on regaining it, reloaded. See
        // UpdateHomeVideoState.
        Activated += (_, _) => CrashLog.Guard(UpdateHomeVideoState, "activated");
        Deactivated += (_, _) => CrashLog.Guard(UpdateHomeVideoState, "deactivated");
        // CoverEntireMonitor() in the Opened handler above only covers startup. If the display then
        // changes at runtime (e.g. the ROG Ally switching between its built-in screen and an external
        // monitor without closing the app), Avalonia does not fire Opened again - the window kept the
        // previous display's size and position. Screens raises Changed whenever the attached-display
        // configuration changes (resolution, monitor added/removed), so we recompute there too.
        Screens.Changed += (_, _) => CrashLog.Guard(CoverEntireMonitor, "screens-changed");

        // A shell must always be exactly the monitor. If anything maximizes the window, take it back.
        //
        // Why this is not theoretical: MAXIMIZED IS NOT THE SAME SIZE AS THE MONITOR - Windows makes a
        // maximized window respect the taskbar, so it comes out 1920x1032 instead of 1920x1080. The UI
        // keeps a fixed 16:9 aspect inside a Viewbox, so those 48 missing rows shrink the whole canvas
        // to 95.6% and leave BLACK BANDS down both sides. It then stays that way, because
        // CoverEntireMonitor otherwise only runs at startup.
        //
        // Win+Up does it with one keystroke, and so does any tool that calls ShowWindow(SW_MAXIMIZE).
        // CanResize="False" in the XAML stops the user-driven route; this handles the rest.
        // Minimized is deliberately NOT touched: fighting a minimise would trap the window on screen.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty &&
                (WindowState == WindowState.Maximized || WindowState == WindowState.FullScreen))
            {
                CrashLog.Guard(CoverEntireMonitor, "window-state-changed");
            }
        };

        // Saved background: can be a solid colour, a specific video, or the default dynamic video.
        // ApplyBackground sets the poster (first frame) instantly and, via UpdateHomeVideoState,
        // loads the video only if the home is on screen (not yet at startup - it loads in the
        // Opened/Activated handlers above). It also places My background's "active background" check
        // on the right source (Solid colors or Dynamic backgrounds).
        _backgroundSolidHex = BackgroundSettings.LoadSolidHex();
        _backgroundVideoRelPath = BackgroundSettings.LoadVideoRelPath();
        ApplyBackground();

        StartBatteryMonitor();
        StartClock();

        // Debug shortcut, for screenshots and verification only: if the PLAYFRONT_DEBUG_SCREEN
        // environment variable is set, the app starts directly on that screen instead of the home. No
        // effect in normal use (the variable does not exist). It saves navigating with the
        // gamepad/keyboard to reach a deep screen just to capture it. Runs after the first layout
        // pass (Post) so the screen is already measured when shown.
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

        // No battery reading (e.g. a desktop without a battery): keep the last known state instead
        // of emptying the bar.
        if (_battery.Percent is not { } percent)
        {
            return;
        }

        BatteryFill.Width = BatteryFillMaxWidth * Math.Clamp(percent / 100.0, 0.0, 1.0);

        // With the charger connected, show the charging icon (notched outline + bolt, from Xbox's
        // Battery4Charging.svg); without it, the normal outline (Battery0). The green fill showing
        // the percentage is the same in both cases.
        var charging = _battery.IsPluggedIn;
        BatteryOutline.IsVisible = !charging;
        BatteryOutlineCharging.IsVisible = charging;
        BatteryBolt.IsVisible = charging;
    }

    // Avalonia's WindowState="FullScreen" only covers the work area (the screen minus the Windows
    // taskbar), not the whole monitor. That left black bands at the sides because the UI keeps a
    // fixed 16:9 aspect. Here the monitor's exact size and position are forced, covering the taskbar.
    //
    // Important: no Topmost. With Topmost=true the window always draws above any other app even when
    // it loses focus (e.g. on Alt+Tab), which made it impossible to get out of the app even though
    // the window switch was working underneath. Without Topmost, another window coming to the front
    // sits in front normally.
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

        // Geometry goes to the log because "black bands down the sides" is the one visual fault that
        // cannot be diagnosed from a description: the UI keeps a fixed 16:9 aspect, so the bands mean
        // the window is not the shape of the monitor, and only these numbers say why.
        CrashLog.Info($"CoverEntireMonitor: bounds={screen.Bounds} workingArea={screen.WorkingArea} " +
                      $"scaling={screen.Scaling} -> asked for {Width}x{Height}");
        Dispatcher.UIThread.Post(
            () => CrashLog.Info($"CoverEntireMonitor: window ended up {ClientSize.Width}x{ClientSize.Height}"),
            DispatcherPriority.Loaded);
    }

    // Path of the video currently playing as the background (so the control is not recreated when it
    // does not change).
    private string? _currentVideoPath;

    // Sets (or changes) the home's background video. If it is a DIFFERENT video and a player already
    // exists, only its source is swapped (SetVideoSource) - no destroy/recreate - so applying another
    // background is INSTANT, without the stall of tearing down the decoder. The control is only
    // removed when the video genuinely has to be unloaded (solid background, or the app loses the
    // foreground: fullPath null or non-existent).
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

    // TEMPORARY: the "Game 1" tile acts as the install-Steam button. It asks the helper service
    // (SYSTEM) to install it (download + verify signature + install without UAC); if it is already
    // installed, it says so. The label tracks the status. Moves to its proper place when the library
    // is built.
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
            // Keyboard "back": Backspace and ESCAPE do the same as the gamepad's B.
            // Escape was added because it is the first thing anyone tries to leave a screen, and
            // until then it did NOTHING: without a gamepad to hand there was no way to go back.
            // Note it is "back", NOT "close the app": the Escape that used to close the app was
            // removed on purpose and is not coming back.
            case Key.Back:
            case Key.Escape:
                Move(GamepadButton.B);
                break;
            // LB/RB bumpers on the keyboard (for testing without a gamepad): Q and E.
            case Key.Q:
                Move(GamepadButton.LB);
                break;
            case Key.E:
                Move(GamepadButton.RB);
                break;
        }
    }

    // The app's update engine, a single shared instance used by the System -> Updates screen. It
    // lives here and not in the screen because the state must survive closing it: download an update
    // and leave, and on returning it is still ready to apply instead of starting over.
    private readonly UpdateService _updates = new();

    private void Move(GamepadButton button)
    {
        // The order here decides which screen receives the gamepad: checked topmost first. "My color
        // & theme" sits above Personalization, which sits above Settings; all of them stay mounted
        // behind (with their _inX true) so that closing with B reveals each one exactly as it was.
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

        // "General - Home" hangs off Personalization, so it goes above it.
        if (_inPersonalizationHome)
        {
            MovePersonalizationHome(button);
            return;
        }

        // "Solid colors - Custom" hangs off "Solid colors", so it goes above it.
        if (_inCustomColor)
        {
            MoveCustomColor(button);
            return;
        }

        // "Solid colors" hangs off "My background", so it goes above it.
        if (_inSolidColors)
        {
            MoveSolidColors(button);
            return;
        }

        // "Custom image" also hangs off "My background" (visual-only screen).
        if (_inCustomImage)
        {
            MoveCustomImage(button);
            return;
        }

        // "Dynamic backgrounds" also hangs off "My background".
        if (_inDynamic)
        {
            MoveDynamic(button);
            return;
        }

        // "My background" also hangs off Personalization (sibling of "My color & theme"), so it is
        // checked before it. The two are never open at the same time.
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

        // "System Updates" and "Console info" hang off Settings and sit ABOVE it, so they are checked
        // first.
        if (_inConsoleInfo)
        {
            _consoleInfoView?.Move(button);
            return;
        }

        if (_inStorage)
        {
            _storageView?.Move(button);
            return;
        }

        if (_inLanguage)
        {
            _languageView?.Move(button);
            return;
        }

        if (_inTime)
        {
            _timeView?.Move(button);
            return;
        }

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

        // The Store is a full screen (like Settings): while it is open the gamepad navigates it. Its
        // view decides when to leave (B at the top level -> ExitStore). The category page sits ABOVE
        // the Store: if it is open, the gamepad is its. YouTube (a full-screen web app) sits ABOVE
        // everything: if it is open, the gamepad is its.
        if (_inYouTube)
        {
            MoveYouTube(button);
            return;
        }

        // The product page sits ABOVE the category page, so it is checked first.
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

        // The Library is a full screen (like Settings/Store): while it is open the gamepad navigates
        // it. For now it only exits with B.
        if (_inLibrary)
        {
            _libraryView?.Move(button);
            return;
        }

        switch (button)
        {
            // The "My games & apps" icon is the FIRST (column 0) of the navigation row (row 0).
            case GamepadButton.A when _row == 0 && _col == 0:
                EnterLibrary();
                return;
            // The Settings icon is the last (column 4) of the navigation row (row 0).
            case GamepadButton.A when _row == 0 && _col == 4:
                EnterSettings();
                return;
            // The Store icon is column 1 (the bag) of the navigation row (row 0).
            case GamepadButton.A when _row == 0 && _col == 1:
                EnterStore();
                return;
            // TEMPORARY: the "Game 1" tile (row 1, col 0) acts as the "Install Steam" button for now.
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

        // _inSettings is set here (before any "await") so that if the gamepad keeps being used while
        // the loading gear is on screen, input already navigates inside Settings instead of firing
        // EnterSettings a second time.
        _inSettings = true;
        _settingsTransitioning = true;
        UpdateHomeVideoState(); // home covered by Settings: pause the background video (not visible)

        // Wrapped so a failure mid-transition does NOT leave the black veil stuck on screen or the
        // _settingsTransitioning flag latched (that would block Settings forever).
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
            // If it failed BEFORE the view was mounted, do not leave us "inside Settings" with no
            // screen (input would go nowhere and B would not get out): fall back cleanly to the home.
            if (_settingsView == null && _inSettings)
            {
                _inSettings = false;
                UpdateHomeVideoState();
                UpdateSelection();
            }
        }
    }

    // Settings entry sequence (loading veil + on-demand view construction). Split out so that
    // EnterSettings can wrap it in try/finally (see there).
    private async Task RunEnterSettingsTransition()
    {
        var stopwatch = Stopwatch.StartNew();

        SettingsLoadingScreen.IsVisible = true;
        // Let Avalonia paint one frame at opacity 0 before raising it to 1 - if both assignments
        // landed in the same frame, the fade-in would not be visible (it would jump from invisible
        // to opaque instead of animating).
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        SettingsLoadingScreen.Opacity = 1;

        // Important: Settings is not prepared behind the veil until the fade-in has fully finished
        // (veil 100% opaque). Doing it earlier would show Settings "through" the veil while it is
        // still semi-transparent, instead of appearing cleanly behind solid black.
        await Task.Delay(SettingsLoadingFadeDuration);

        // Build the Settings view HERE (under the fully opaque black veil, so the construction is
        // never visible) and release it on exit (ExitSettings): the Home is the only resident screen
        // at startup. The view starts in its default state ("General" category) and draws itself in
        // its constructor.
        _settingsView = new SettingsView();
        _settingsView.PersonalizationRequested += EnterPersonalization;
        _settingsView.UpdatesRequested += EnterUpdates;
        _settingsView.ConsoleInfoRequested += EnterConsoleInfo;
        _settingsView.StorageRequested += EnterStorage;
        _settingsView.LanguageRequested += EnterLanguage;
        _settingsView.TimeRequested += EnterTime;
        _settingsView.ExitRequested += ExitSettings;
        SettingsHost.Children.Add(_settingsView);

        // Wait for Avalonia to complete a layout/render pass of the newly shown Settings screen
        // (relevant when a heavy game is hogging CPU/GPU and it takes a while to draw), and also
        // honour the 300ms minimum above - whichever of the two takes longer.
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

    // Leaving Settings: straight back to the Home, no loading veil. The gear veil is only used on
    // entry (where the Settings screen's construction has to be covered); the Home is already
    // mounted behind, so leaving has nothing to cover.
    private void ExitSettings()
    {
        if (_settingsTransitioning)
        {
            return;
        }

        _inSettings = false;

        // Safety net: if Settings is somehow left with one of its sub-screens still mounted, close it
        // too. Without this an orphan screen would sit over the home with nobody able to navigate or
        // close it.
        if (_inUpdates)
        {
            ExitUpdates();
        }

        if (_inConsoleInfo)
        {
            ExitConsoleInfo();
        }

        if (_inStorage)
        {
            ExitStorage();
        }

        if (_inLanguage)
        {
            ExitLanguage();
        }

        if (_inTime)
        {
            ExitTime();
        }

        if (_settingsView != null)
        {
            _settingsView.PersonalizationRequested -= EnterPersonalization;
            _settingsView.UpdatesRequested -= EnterUpdates;
            _settingsView.ConsoleInfoRequested -= EnterConsoleInfo;
            _settingsView.StorageRequested -= EnterStorage;
            _settingsView.LanguageRequested -= EnterLanguage;
            _settingsView.TimeRequested -= EnterTime;
            _settingsView.ExitRequested -= ExitSettings;
            SettingsHost.Children.Remove(_settingsView);
            _settingsView = null; // release the view: the garbage collector reclaims its memory
        }
        UpdateHomeVideoState(); // back on the home: resume the background video
        UpdateSelection();
    }

    // Entering/leaving the STORE. No loading veil for now (the view is light, just the background).
    // When the content grows (real images) a veil will be added to cover the construction, as in
    // EnterSettings. The view is built on demand and released on exit.
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
        UpdateHomeVideoState(); // home covered by the Store: pause the background video (not visible)
    }

    // ===== Store category page (Apps > Music apps) =====
    // Mounted over the Store and HIDES its host while up: the page is opaque and painting the Store
    // underneath would be wasted work. The Store stays mounted (not released) so B returns to it
    // without rebuilding it or losing its position.
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
            _categoryView = null; // release the view and its art
        }

        StoreHost.IsVisible = true;
    }

    // ===== App product page (Music apps > YouTube) =====
    // One level above the category page, same pattern: built on entry, released on exit, and hides
    // the screen below while up.
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

    // Primary button (INSTALL/PLAY) of an app page. Only YouTube has behaviour so far: it launches
    // its web app. Persisting the "installed" state (record + PLAY button + tile) comes next; for
    // now the button already OPENS YouTube, which is the part worth testing.
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
            _appView = null; // release the view and its art
        }

        CategoryHost.IsVisible = true;
    }

    // ===== YouTube web app (TV interface, inside a WebView2) =====
    // Mounted full screen ABOVE everything (YouTubeHost lives outside the scaled Viewbox). The
    // browser is a native window that always draws on top, so there is no app UI overlaid here: it
    // is YouTube full screen. Fully released on exit (the browser processes go away). See
    // src/Playfront.App/Web/WebViewHost.cs.
    private bool _inYouTube;
    private Web.WebViewHost? _youTube;

    // Browser profile folder for YouTube: cookies and the session (persistent login) live here.
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

        // Park the rest: drop the home's background video (do not run two video pipelines at once)
        // and enable gamepad auto-repeat to move through long rails quickly.
        UpdateHomeVideoState();
        _gamepad.RepeatEnabled = true;
    }

    private void ExitYouTube()
    {
        _inYouTube = false;
        if (_youTube != null)
        {
            _youTube.InitFailed -= OnYouTubeInitFailed;
            YouTubeHost.Children.Remove(_youTube); // fires DestroyNativeControlCore -> closes the browser
            _youTube = null;
        }

        YouTubeHost.IsVisible = false;
        _gamepad.RepeatEnabled = false;
        UpdateHomeVideoState(); // resume the home background
    }

    private void OnYouTubeInitFailed(string message)
    {
        // The most likely cause is a missing WebView2 runtime (already installed here). For now this
        // only logs and leaves the screen; installing the runtime via the helper comes later.
        CrashLog.Log($"WebView2 failed to initialize: {message}", null);
        ExitYouTube();
    }

    // Translates the gamepad into the keys YouTube's Leanback interface understands and injects them
    // into the page (standard JS keyCodes). Map traced from the Xbox YouTube app (sources: YouTube
    // Help for Xbox Series X|S and Xbox One):
    //   - D-pad/stick = navigate; INSIDE the video, left/right = seek back/forward (Leanback does
    //     that itself with the arrows, no separate mapping needed).
    //   - A = select (Enter).
    //   - B = back. On Xbox there is no "leave the app" on the gamepad (the Xbox button does it);
    //     here, returning to the shell is done by HOLDING B (BHeld event -> ExitYouTube, see the
    //     constructor).
    //   - Y = search.
    // Our addition (Xbox does this through the player bar, which is clumsier): X and Start =
    // play/pause.
    private void MoveYouTube(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.Up: _youTube?.SendKey(38); break;
            case GamepadButton.Down: _youTube?.SendKey(40); break;
            case GamepadButton.Left: _youTube?.SendKey(37); break;
            case GamepadButton.Right: _youTube?.SendKey(39); break;
            case GamepadButton.A: _youTube?.SendKey(13); break;    // Enter = select
            case GamepadButton.B: _youTube?.SendKey(27); break;    // Escape = back (inside YouTube)
            // Search with the ON-SCREEN KEYBOARD. keyCode 170 (the "asterisk" the Leanback interface
            // uses for its keyboard search): confirmed in VacuumTube, the youtube.com/tv wrapper. The
            // 191 ("/") tried earlier opened VOICE search, which is not what we want.
            case GamepadButton.Y: _youTube?.SendKey(170); break;
            case GamepadButton.X: _youTube?.SendKey(32); break;    // Space = play/pause
            case GamepadButton.Start: _youTube?.SendKey(32); break; // Space = play/pause (redundant, handy)
            case GamepadButton.LT: _youTube?.SendKey(113); break;  // F2 = seek back in the video
            case GamepadButton.RT: _youTube?.SendKey(114); break;  // F3 = seek forward in the video
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
            _storeView = null; // release the view: the garbage collector reclaims its memory
        }
        UpdateHomeVideoState(); // back on the home: resume the background video
        UpdateSelection();
    }

    // Entering/leaving the LIBRARY ("My games & apps"). Same pattern as the Store: the view is built
    // on demand and released on exit. No loading veil for now (the view is light).
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
        UpdateHomeVideoState(); // home covered by the Library: pause the background video (not visible)
    }

    private void ExitLibrary()
    {
        _inLibrary = false;
        if (_libraryView != null)
        {
            _libraryView.ExitRequested -= ExitLibrary;
            LibraryHost.Children.Remove(_libraryView);
            _libraryView = null; // release the view: the garbage collector reclaims its memory
        }
        UpdateHomeVideoState(); // back on the home: resume the background video
        UpdateSelection();
    }

    // Entering/leaving Personalization carries no loading veil (unlike opening Settings from the
    // home): the screen is already mounted behind, there is nothing heavy to prepare and therefore
    // nothing to cover.
    // "System Updates" hangs off the "Updates" card of Settings > System. Built on demand and
    // released on exit, like the Settings view itself: the Home is the only resident screen. While
    // it is up, SettingsHost is hidden - the screen is opaque and painting Settings underneath would
    // be wasted work.
    // "Time" hangs off Settings > System, like the rest of its sub-screens.
    private void EnterTime()
    {
        if (_timeView != null)
        {
            return;
        }

        _inTime = true;
        _timeView = new SystemTimeView();
        _timeView.ExitRequested += ExitTime;
        UpdatesHost.Children.Add(_timeView);
        SettingsHost.IsVisible = false;
    }

    private void ExitTime()
    {
        _inTime = false;
        SettingsHost.IsVisible = true;

        if (_timeView != null)
        {
            _timeView.ExitRequested -= ExitTime;
            UpdatesHost.Children.Remove(_timeView);
            _timeView = null;
        }
    }

    // "Language & location" hangs off Settings > System, like the rest of its sub-screens.
    private void EnterLanguage()
    {
        if (_languageView != null)
        {
            return;
        }

        _inLanguage = true;
        _languageView = new SystemLanguageView();
        _languageView.ExitRequested += ExitLanguage;
        UpdatesHost.Children.Add(_languageView);
        SettingsHost.IsVisible = false;
    }

    private void ExitLanguage()
    {
        _inLanguage = false;
        SettingsHost.IsVisible = true;

        if (_languageView != null)
        {
            _languageView.ExitRequested -= ExitLanguage;
            UpdatesHost.Children.Remove(_languageView);
            _languageView = null;
        }
    }

    // "Storage devices" hangs off Settings > System, like Updates and Console info.
    private void EnterStorage()
    {
        if (_storageView != null)
        {
            return;
        }

        _inStorage = true;
        _storageView = new SystemStorageView();
        _storageView.ExitRequested += ExitStorage;
        UpdatesHost.Children.Add(_storageView);
        SettingsHost.IsVisible = false;
    }

    private void ExitStorage()
    {
        _inStorage = false;
        SettingsHost.IsVisible = true;

        if (_storageView != null)
        {
            _storageView.ExitRequested -= ExitStorage;
            UpdatesHost.Children.Remove(_storageView);
            _storageView = null;
        }
    }

    // "Console info" hangs off Settings > System, like Updates: mounted on demand, released on B.
    private void EnterConsoleInfo()
    {
        if (_consoleInfoView != null)
        {
            return;
        }

        _inConsoleInfo = true;
        _consoleInfoView = new SystemConsoleInfoView();
        _consoleInfoView.ExitRequested += ExitConsoleInfo;
        UpdatesHost.Children.Add(_consoleInfoView);
        SettingsHost.IsVisible = false;
    }

    private void ExitConsoleInfo()
    {
        _inConsoleInfo = false;
        SettingsHost.IsVisible = true;

        if (_consoleInfoView != null)
        {
            _consoleInfoView.ExitRequested -= ExitConsoleInfo;
            UpdatesHost.Children.Remove(_consoleInfoView);
            _consoleInfoView = null;
        }
    }

    private void EnterUpdates()
    {
        if (_updatesView != null)
        {
            return;
        }

        _inUpdates = true;
        // It is handed the SAME service the rest of the app uses: with one per screen, every visit
        // would lose the download in progress and hit the network again from scratch.
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
        _pzCol = 0;
        _pzRow = 0;
        PersonalizationScreen.IsVisible = true;
        UpdateHomeVideoState(); // home covered by Personalization (and its subscreens): pause the video
        UpdatePersonalizationSelection();
    }

    private void ExitPersonalization()
    {
        _inPersonalization = false;
        PersonalizationScreen.IsVisible = false;
        UpdateHomeVideoState(); // back on the home: resume the background video
    }

    private void MovePersonalization(GamepadButton button)
    {
        switch (button)
        {
            // The two tiles that already lead somewhere, addressed by (column, row) rather than by
            // a flat index: the reference has reshuffled these tiles once already, and a position
            // survives that better than a number.
            case GamepadButton.A when _pzCol == 0 && _pzRow == 0:   // Home
                EnterPersonalizationHome();
                return;
            case GamepadButton.A when _pzCol == 1 && _pzRow == 1:   // My color & theme
                EnterColorTheme();
                return;
            case GamepadButton.A when _pzCol == 1 && _pzRow == 2:   // My background
                EnterMyBackground();
                return;
            case GamepadButton.B:
                ExitPersonalization();
                return;
            case GamepadButton.Up when _pzRow > 0:
                _pzRow--;
                break;
            case GamepadButton.Down when _pzRow < _personalizationTiles[_pzCol].Length - 1:
                _pzRow++;
                break;
            case GamepadButton.Left when _pzCol > 0:
                _pzCol--;
                break;
            case GamepadButton.Right when _pzCol < _personalizationTiles.Length - 1:
                _pzCol++;
                break;
            default:
                return;
        }

        // The right-hand column is one tile tall. Moving into it from a lower row would land on a
        // tile that does not exist, so the row is clamped to what the new column actually has.
        var alto = _personalizationTiles[_pzCol].Length;
        if (_pzRow > alto - 1) _pzRow = alto - 1;

        UpdatePersonalizationSelection();
    }

    // Entering/leaving "General - Home". Focus starts on the first control, as the reference does.
    private void EnterPersonalizationHome()
    {
        _phControls ??= new[] { PhCtrl0, PhCtrl1, PhCtrl2, PhCtrl3, PhCtrl4 };
        _phRings ??= new[] { PhRing0, PhRing1, PhRing2, PhRing3, PhRing4 };

        _inPersonalizationHome = true;
        _phIndex = 0;
        PersonalizationHomeScreen.IsVisible = true;
        UpdatePersonalizationHomeSelection();
    }

    private void ExitPersonalizationHome()
    {
        _inPersonalizationHome = false;
        PersonalizationHomeScreen.IsVisible = false;
    }

    // Right from the third column lands on Edit GAMES, not Edit groups: the fourth column has two
    // rows and the move keeps the row it came from (both sit at y=821).
    private void MovePersonalizationHome(GamepadButton button)
    {
        // While the dropdown is open it owns the gamepad.
        if (_phTilesOpen)
        {
            MoveTilesDropdown(button);
            return;
        }

        var i = _phIndex;
        switch (button)
        {
            case GamepadButton.A when i == 1: // the tile count is the only control that opens
                OpenTilesDropdown();
                return;
            case GamepadButton.B:
                ExitPersonalizationHome();
                return;
            case GamepadButton.Right when i < 2:
                i++;
                break;
            case GamepadButton.Right when i == 2:
                i = 4;
                break;
            case GamepadButton.Left when i == 3 || i == 4:
                i = 2;
                break;
            case GamepadButton.Left when i > 0:
                i--;
                break;
            case GamepadButton.Up when i == 4:
                i = 3;
                break;
            case GamepadButton.Down when i == 3:
                i = 4;
                break;
            default:
                return; // includes A: nothing here does anything yet
        }

        _phIndex = i;
        UpdatePersonalizationHomeSelection();
    }

    private void UpdatePersonalizationHomeSelection()
    {
        if (_phControls is null || _phRings is null)
        {
            return;
        }

        for (var i = 0; i < _phControls.Length; i++)
        {
            var selected = i == _phIndex;
            _phControls[i].Classes.Set("selected", selected);
            _phRings[i].Classes.Set("selected", selected);
        }

        // The strip slides only while focus is in the fourth column. The transition is declared on the
        // Canvas in the XAML, so assigning the transform animates it.
        var shift = _phIndex >= 3 ? -PhStripShift : 0;
        PhStrip.RenderTransform = TransformOperations.Parse($"translateX({shift.ToString(CultureInfo.InvariantCulture)}px)");
    }

    // Opening the tile-count dropdown. The panel REPLACES its card, and is placed so the active
    // option lands in the slot that card occupied - which is why it reads as opening upwards with the
    // last option chosen. Same mechanism as the theme dropdown on "My color & theme".
    private void OpenTilesDropdown()
    {
        _phTilesRows ??= new[] { PhOpt0, PhOpt1, PhOpt2, PhOpt3, PhOpt4, PhOpt5 };
        _phTilesRings ??= new[] { PhOptRing0, PhOptRing1, PhOptRing2, PhOptRing3, PhOptRing4, PhOptRing5 };

        _phTilesOpen = true;
        _phTilesPrevValue = PhTilesValue.Text ?? PhTilesOptions[^1];
        _phTilesOption = Array.IndexOf(PhTilesOptions, _phTilesPrevValue);
        if (_phTilesOption < 0) _phTilesOption = PhTilesOptions.Length - 1;

        PhCtrl1.IsVisible = false;
        PhRing1.IsVisible = false;

        var top = PhTilesSlotTop - _phTilesOption * PhTilesRowHeight;
        Canvas.SetTop(PhTilesDropdown, top);
        for (var i = 0; i < _phTilesRings.Length; i++)
        {
            // Ring = row inflated 8. The rows are laid out from the panel's OUTER top, not from
            // inside its border: the reference draws that 2 px border over the first row, so the row
            // Canvas.Tops in the XAML carry a -2 to cancel the Border's own inset.
            Canvas.SetTop(_phTilesRings[i], top + i * PhTilesRowHeight - 8);
        }

        // Start one row tall in the card's slot with the list shifted so the row on show is the
        // active one; the panel's own translate cancels the difference between the two tops. Setting
        // the end state in this same pass would skip the motion entirely.
        var offset = _phTilesOption * PhTilesRowHeight;
        PhTilesDropdown.Height = PhTilesRowHeight + 4;
        PhTilesDropdown.RenderTransform = TransformOperations.Parse(
            $"translateY({offset.ToString(CultureInfo.InvariantCulture)}px)");
        PhTilesList.RenderTransform = TransformOperations.Parse(
            $"translateY({(-offset).ToString(CultureInfo.InvariantCulture)}px)");
        PhTilesDropdown.IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            PhTilesDropdown.Height = PhTilesRowHeight * PhTilesOptions.Length + 4;
            PhTilesDropdown.RenderTransform = TransformOperations.Parse("translateY(0px)");
            PhTilesList.RenderTransform = TransformOperations.Parse("translateY(0px)");
        });

        UpdateTilesDropdown();
    }

    private void CloseTilesDropdown()
    {
        _phTilesOpen = false;
        PhTilesDropdown.IsVisible = false;
        if (_phTilesRings is not null)
        {
            foreach (var ring in _phTilesRings)
            {
                ring.IsVisible = false;
            }
        }

        PhCtrl1.IsVisible = true;
        PhRing1.IsVisible = true;
    }

    private void MoveTilesDropdown(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.A:
                PhTilesValue.Text = PhTilesOptions[_phTilesOption];
                CloseTilesDropdown();
                return;
            case GamepadButton.B:
                PhTilesValue.Text = _phTilesPrevValue;
                CloseTilesDropdown();
                return;
            case GamepadButton.Up when _phTilesOption > 0:
                _phTilesOption--;
                break;
            case GamepadButton.Down when _phTilesOption < PhTilesOptions.Length - 1:
                _phTilesOption++;
                break;
            default:
                return;
        }

        UpdateTilesDropdown();
    }

    private void UpdateTilesDropdown()
    {
        if (_phTilesRows is null || _phTilesRings is null)
        {
            return;
        }

        for (var i = 0; i < _phTilesRows.Length; i++)
        {
            var isSelected = i == _phTilesOption;
            _phTilesRows[i].Background = isSelected
                ? new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45))
                : Brushes.Transparent;
            _phTilesRings[i].IsVisible = isSelected;
            _phTilesRings[i].Classes.Set("selected", isSelected);
        }
    }

    // Entering/leaving "My color & theme": the screen is already mounted, no loading veil (same as
    // Personalization). Closing with B returns to Personalization exactly as it was.
    private void EnterColorTheme()
    {
        _inColorTheme = true;
        _colorThemeIndex = 0;
        ColorThemeScreen.IsVisible = true;
        UpdateColorThemeSelection();
    }

    private void ExitColorTheme()
    {
        // Leaving with the dropdown still open would come back to it open on re-entry, with the
        // "System theme" card still hidden.
        if (_themeOpen) CloseThemeDropdown();
        _inColorTheme = false;
        ColorThemeScreen.IsVisible = false;
    }

    // "System theme" dropdown: while it is open the gamepad drives the three options and not the
    // two cards behind, so it is checked FIRST in MoveColorTheme - same rule as every other screen
    // that sits on top of another.
    private bool _themeOpen;
    private int _themeOption;

    private const double ThemeRowHeight = 56.33;
    // Where the closed "System theme" card sits. The dropdown lines its selected row up with this.
    private const double CardSlotTop = 404;
    private static readonly string[] ThemeOptions = { "Dark", "Light", "Scheduled" };

    private void OpenThemeDropdown()
    {
        _themeOpen = true;
        _themeOption = Array.IndexOf(ThemeOptions, CtThemeValue.Text);
        if (_themeOption < 0) _themeOption = 0;

        // The card it replaces disappears, exactly as in the reference. The schedule cards do NOT:
        // they stay on screen and the panel is simply drawn over them (hence its ZIndex).
        CtCard1.IsVisible = false;
        CtRing1.IsVisible = false;

        // WHERE THE PANEL GOES. The selected option always lands in the slot the closed card
        // occupied (y 404), so the list is positioned around it and the panel opens UPWARDS when the
        // selection is not the first. Measured on the reference with "Scheduled" (the third) chosen:
        // its row sits at exactly the same pixels the first option occupied when "Dark" was chosen.
        var top = CardSlotTop - _themeOption * ThemeRowHeight;
        Canvas.SetTop(CtThemeDropdown, top);

        var anillos = new[] { CtOptRing0, CtOptRing1, CtOptRing2 };
        for (var i = 0; i < anillos.Length; i++)
            Canvas.SetTop(anillos[i], top + i * ThemeRowHeight - 8);   // ring = row inflated 8

        // THE ANIMATION, and it needs two moves at once. Start: the panel is one row tall sitting in
        // the card's slot, with the list shifted so that the row on show is the selected one. End:
        // full height, no shift. The panel's own translate cancels the offset between the two tops,
        // so what the eye follows is the selected row staying still while the rest unfolds around
        // it. Setting the end state in the same pass as IsVisible would skip the motion entirely.
        var desfase = _themeOption * ThemeRowHeight;
        CtThemeDropdown.Height = ThemeRowHeight;
        CtThemeDropdown.RenderTransform = TransformOperations.Parse(
            $"translateY({desfase.ToString(CultureInfo.InvariantCulture)}px)");
        CtThemeList.RenderTransform = TransformOperations.Parse(
            $"translateY({(-desfase).ToString(CultureInfo.InvariantCulture)}px)");
        CtThemeDropdown.IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            CtThemeDropdown.Height = ThemeRowHeight * ThemeOptions.Length;
            CtThemeDropdown.RenderTransform = TransformOperations.Parse("translateY(0px)");
            CtThemeList.RenderTransform = TransformOperations.Parse("translateY(0px)");
        });

        UpdateThemeDropdown();
    }

    private void CloseThemeDropdown()
    {
        _themeOpen = false;
        CtThemeDropdown.IsVisible = false;
        CtThemeDropdown.Height = 0;
        CtOptRing0.IsVisible = false;
        CtOptRing1.IsVisible = false;
        CtOptRing2.IsVisible = false;
        CtCard1.IsVisible = true;
        CtRing1.IsVisible = true;
        UpdateScheduleCards();      // brings them back if the value is still "Scheduled"
        UpdateColorThemeSelection();
    }

    // The three schedule cards only exist while the theme is "Scheduled", exactly as in the
    // reference - and so does the ability to move onto them.
    private int ColorThemeCount => CtThemeValue.Text == "Scheduled" ? 5 : 2;

    private void UpdateScheduleCards()
    {
        var visible = CtThemeValue.Text == "Scheduled";
        CtSched0.IsVisible = visible;
        CtSched1.IsVisible = visible;
        CtSched2.IsVisible = visible;

        // Switching away from "Scheduled" while sitting on one of them would leave the selection on
        // a card that is no longer there.
        if (!visible && _colorThemeIndex > 1) _colorThemeIndex = 1;
    }

    private void UpdateThemeDropdown()
    {
        var filas = new[] { CtOpt0, CtOpt1, CtOpt2 };
        var anillos = new[] { CtOptRing0, CtOptRing1, CtOptRing2 };
        for (var i = 0; i < filas.Length; i++)
        {
            var isSelected = i == _themeOption;
            filas[i].Background = isSelected ? new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45)) : Brushes.Transparent;
            anillos[i].IsVisible = isSelected;
            anillos[i].Classes.Set("selected", isSelected);
        }
    }

    private void MoveColorTheme(GamepadButton button)
    {
        if (_themeOpen)
        {
            switch (button)
            {
                case GamepadButton.A:
                    // Picking only writes the label for now; nothing switches theme yet.
                    CtThemeValue.Text = ThemeOptions[_themeOption];
                    UpdateScheduleCards();
                    CloseThemeDropdown();
                    return;
                case GamepadButton.B:
                    CloseThemeDropdown();
                    return;
                case GamepadButton.Up when _themeOption > 0:
                    _themeOption--;
                    break;
                case GamepadButton.Down when _themeOption < ThemeOptions.Length - 1:
                    _themeOption++;
                    break;
                default:
                    return;
            }

            UpdateThemeDropdown();
            return;
        }

        switch (button)
        {
            // "My color" (card 0) opens the colour picker.
            case GamepadButton.A when _colorThemeIndex == 0:
                EnterColorPicker();
                return;
            // "System theme" (card 1) unfolds its three options in place. It still does this while
            // the schedule cards are showing, and the dropdown then covers them.
            case GamepadButton.A when _colorThemeIndex == 1:
                OpenThemeDropdown();
                return;
            // The schedule cards can be reached and highlighted, but A does nothing on them yet.
            case GamepadButton.A:
                return;
            case GamepadButton.B:
                ExitColorTheme();
                return;
            case GamepadButton.Up when _colorThemeIndex > 0:
                _colorThemeIndex--;
                break;
            case GamepadButton.Down when _colorThemeIndex < ColorThemeCount - 1:
                _colorThemeIndex++;
                break;
            default:
                return;
        }

        UpdateColorThemeSelection();
    }

    // Builds the 14 colour swatches and their rings inside SwatchHost. 7x2 grid: 243x207 each,
    // columns every 258 from x=99, row 1 at y=313 and row 2 at y=536 (measured from the reference at
    // its capture factor, 1.3502). Called once from the constructor.
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

        // The rings are added AFTER all the swatches so they render on top (their halo is not covered
        // by the neighbouring swatch). Same accent ring as the rest of Settings.
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

        // "Applied colour" mark: white triangle (#EBEBEB) in the top right corner (legs ~68) + a thin
        // dark check on top. Measured from the reference. Added last so it renders above swatches and
        // rings; it is positioned/hidden in RefreshAppliedCheck.
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

    // Entering/leaving the colour picker. Closing (B or OK) returns to "My color & theme".
    private void EnterColorPicker()
    {
        _inColorPicker = true;
        // Focus starts on the currently applied colour (if it is one of the 14); otherwise the
        // first one.
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
        var i = _colorPickerIndex; // 0..13 = swatches, 14 = OK
        switch (button)
        {
            case GamepadButton.B:
                ExitColorPicker();
                return;
            case GamepadButton.A when i == 14: // OK -> close
                ExitColorPicker();
                return;
            case GamepadButton.A when i < 14: // pick this colour: applied live across the whole app
                ApplyAccent(AccentTheme.Palette[i].Hex);
                return; // stay in the picker so the change is visible and others can be tried
            case GamepadButton.Left when i < 14 && i % 7 > 0:
                i--;
                break;
            case GamepadButton.Right when i < 14 && i % 7 < 6:
                i++;
                break;
            case GamepadButton.Up when i >= 7 && i < 14:
                i -= 7;
                break;
            case GamepadButton.Up when i == 14: // OK -> row 2, first column
                i = 7;
                break;
            case GamepadButton.Down when i < 7:
                i += 7;
                break;
            case GamepadButton.Down when i >= 7 && i < 14: // row 2 -> OK
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

    // Applies an accent colour across the WHOLE app LIVE (via dynamic resources: selection
    // borders/rings, their halos, Settings highlights, the "My color" circle...) and persists it for
    // the next session. The battery does NOT follow the theme: it stays fixed green.
    private void ApplyAccent(string hex)
    {
        AccentTheme.Apply(Application.Current!, Color.Parse(hex));
        AccentTheme.Save(hex);
        _currentAccentHex = hex;
        CtColorValue.Text = AccentTheme.NameFor(hex);
        RefreshAppliedCheck();
    }

    // Index (0..13) of the swatch with that colour, or -1 if it is none of the 14 (e.g. the default
    // green #439941, which is not in the grid).
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

    // Places the "applied" mark (check) over the swatch of the current accent. If the accent is none
    // of the 14, hides the check. (The title is NOT touched here: it follows the FOCUSED colour, not
    // the applied one - see UpdateColorPickerTitle.)
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

    // The title shows the colour currently FOCUSED (the swatch under the cursor), changing as you
    // move through the grid: "My color - <name>". With focus on the OK button it shows the applied
    // colour (or just "My color" if the applied one is not in the palette).
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

    // Description shown on the right, per selected card.
    //
    // [1] is the reference's own wording, captured with "System theme" selected, and it is what the
    // four-line block on that screen was measured against. IT PROMISES BEHAVIOUR PLAYFRONT DOES NOT
    // HAVE - switching theme on local sunrise/sunset or on a schedule - so it has to be rewritten
    // before this screen ships; it is here to make the layout verifiable.
    // [0] is ours: the reference's "My color" text has not been captured yet.
    private static readonly string[] ColorThemeDescriptions =
    {
        "Choose an accent color for Playfront.",
        "Choose your color scheme. You can also switch between themes based on local sunrise and sunset times, or choose your own schedule.",
    };

    private void UpdateColorThemeSelection()
    {
        for (var i = 0; i < _colorThemeCards.Length; i++)
        {
            var isSelected = i == _colorThemeIndex;
            _colorThemeCards[i].Classes.Set("selected", isSelected);
            _colorThemeRings[i].Classes.Set("selected", isSelected);
        }

        // The schedule cards (2-4) have no captured description of their own yet, so they keep the
        // "System theme" one - which is what the reference showed while they were on screen.
        var desc = _colorThemeIndex < ColorThemeDescriptions.Length ? _colorThemeIndex : ColorThemeDescriptions.Length - 1;
        CtDescription.Text = ColorThemeDescriptions[desc];
    }

    private void UpdatePersonalizationSelection()
    {
        for (var c = 0; c < _personalizationTiles.Length; c++)
        {
            for (var r = 0; r < _personalizationTiles[c].Length; r++)
            {
                var isSelected = c == _pzCol && r == _pzRow;
                _personalizationTiles[c][r].Classes.Set("selected", isSelected);
                _personalizationRings[c][r].Classes.Set("selected", isSelected);
            }
        }
    }

    // Entering/leaving "My background": the screen is already mounted, no loading veil (same as
    // Personalization). Focus starts on "Solid colors" (index 0, as in the reference). Closing with B
    // returns to Personalization exactly as it was.
    private void EnterMyBackground()
    {
        _inMyBackground = true;
        _mbCol = 0;
        _mbRow = 0;
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
        switch (button)
        {
            case GamepadButton.B:
                ExitMyBackground();
                return;
            case GamepadButton.A when _mbCol == 0 && _mbRow == 0:   // Solid colors
                EnterSolidColors();
                return;
            case GamepadButton.A when _mbCol == 0 && _mbRow == 1:   // Custom image
                EnterCustomImage();
                return;
            case GamepadButton.A when _mbCol == 0 && _mbRow == 2:   // Dynamic backgrounds
                EnterDynamic();
                return;
            // "Restore default background": back to the default background (the dynamic video),
            // moving the check there.
            case GamepadButton.A when _mbCol == 1 && _mbRow == 1:
                RestoreDefaultBackground();
                return;
            // "Show selected game art" is new in the reference and does nothing yet.
            case GamepadButton.A:
                return;
            case GamepadButton.Up when _mbRow > 0:
                _mbRow--;
                break;
            case GamepadButton.Down when _mbRow < _myBackgroundTiles[_mbCol].Length - 1:
                _mbRow++;
                break;
            case GamepadButton.Left when _mbCol > 0:
                _mbCol--;
                break;
            case GamepadButton.Right when _mbCol < _myBackgroundTiles.Length - 1:
                _mbCol++;
                break;
            default:
                return;
        }

        // The right column is one row shorter, so coming across from the bottom-left tile would land
        // on a row it does not have.
        var alto = _myBackgroundTiles[_mbCol].Length;
        if (_mbRow > alto - 1) _mbRow = alto - 1;

        UpdateMyBackgroundSelection();
    }

    private void UpdateMyBackgroundSelection()
    {
        for (var c = 0; c < _myBackgroundTiles.Length; c++)
        {
            for (var r = 0; r < _myBackgroundTiles[c].Length; r++)
            {
                var isSelected = c == _mbCol && r == _mbRow;
                _myBackgroundTiles[c][r].Classes.Set("selected", isSelected);
                _myBackgroundRings[c][r].Classes.Set("selected", isSelected);
            }
        }
    }

    // White triangle + check ("active background") in the top right corner of the active background
    // source. Leg ~53 (measured ~71px on the frame x0.75). Same style as the colour picker's "applied
    // colour" mark: #EBEBEB triangle with the right angle at the top right + a thin dark tick centred
    // in its mass. Added to MyBackgroundScreen (above tiles and rings) and repositioned in
    // PositionMyBackgroundCheck.
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

        // Top right corner of the active tile (right edge = Left + width).
        Canvas.SetLeft(_myBackgroundCheck, MbTileLeft + MbTileWidth - 53);
        Canvas.SetTop(_myBackgroundCheck, MbTileTop0 + _myBackgroundActiveIndex * MbTilePitch);
    }

    // Applies the home background from the saved state: with a solid colour, show the colour layer
    // (SolidBackgroundLayer) covering the video; otherwise hide it and the video shows. It also
    // points My background's "active background" check at the right source. Those are ROW numbers in
    // the LEFT column: 0 = Solid colors, 2 = Dynamic backgrounds. "Dynamic" used to be 4, when the
    // column still had "Achievement art" and "Screenshots" in it - leaving it at 4 parked the check
    // two rows below the last tile, floating over the background.
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
            // Poster (first frame) of the active video: shows instantly and sits behind the video, so
            // unloading/reloading the video causes neither a jump nor a black flash.
            if (ResolveHomePosterRelPath() is { } posterRel && LoadPoster(posterRel) is { } poster)
            {
                HomeBackgroundPoster.Source = poster;
                HomeBackgroundPoster.IsVisible = true;
                EvictPostersExceptOnScreen(); // keep only the poster on screen; release the rest (deferred)
            }
            else
            {
                HomeBackgroundPoster.IsVisible = false;
            }
            _myBackgroundActiveIndex = 2;   // Dynamic backgrounds
        }

        // Loads or unloads the video depending on whether the home is on screen (and the background
        // is a video).
        UpdateHomeVideoState();
        PositionMyBackgroundCheck();
    }

    // The home background video decodes while the app is in the foreground (the active window) and
    // the background is a video (not a solid colour). It is FULLY UNLOADED (decoder torn down) only
    // on losing the foreground - entering a game, alt-tab - where freeing the whole GPU matters. On
    // INTERNAL navigation (Settings, Personalization and their subscreens) it is not unloaded, to
    // avoid triggering the delicate teardown on every entry, but decoding IS PAUSED while those
    // opaque screens cover the home: it is not visible, so decoding would only burn GPU and battery.
    // Pausing is instant to undo (the decoder stays alive) and carries none of the teardown risk.
    private bool ShouldHomeVideoRun()
        => IsActive && _backgroundSolidHex is null;

    // The home is COVERED by an opaque full screen: Settings or Personalization (off which hang ALL
    // its subscreens: My background, Dynamic backgrounds, colours...). Besides saving on those
    // screens, this stops TWO videos running at once in Dynamic (the home's, hidden, + the preview):
    // with the home paused, only the preview decodes there.
    private bool IsHomeCovered()
        => _inSettings || _inPersonalization || _inStore || _inYouTube || _inLibrary;

    private void UpdateHomeVideoState()
    {
        if (!ShouldHomeVideoRun())
        {
            SetVideoBackground(null);                   // full unload (game in foreground / solid colour)
            return;
        }

        SetVideoBackground(ResolveHomeVideoPath());     // load/play (no-op if the right one is already set)

        // With the video loaded: pause decoding if the home is covered, resume it when visible.
        if (IsHomeCovered())
        {
            _videoBackground?.Pause();
        }
        else
        {
            _videoBackground?.Resume();
        }
    }

    // Path (relative to Assets/Backgrounds) of the poster (first frame) of the active video
    // background (the chosen one or, by default, the first in the library), or null if the background
    // is a solid colour.
    private string? ResolveHomePosterRelPath()
    {
        if (_backgroundSolidHex is not null)
        {
            return null;
        }

        var bg = _backgroundVideoRelPath is { } rel ? FindBackground(rel) : DefaultBackground();
        return bg?.PosterRelPath;
    }

    // "Restore default background": returns to the default background (the default dynamic video,
    // forgetting any specific video or colour chosen), saves it and repositions the check.
    private void RestoreDefaultBackground()
    {
        _backgroundSolidHex = null;
        _backgroundVideoRelPath = null;
        BackgroundSettings.SaveDynamic();
        ApplyBackground();
    }

    // Entering/leaving the "Solid colors" picker. Focus starts on the applied colour (if the
    // background is one of the 14); otherwise the first one. Closing (B or OK) returns to My
    // background.
    private void EnterSolidColors()
    {
        _inSolidColors = true;
        _solidColorsIndex = Math.Max(0, SolidSlotOf(_backgroundSolidHex));
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
        var i = _solidColorsIndex; // 0..13 = swatches, 14 = OK
        switch (button)
        {
            case GamepadButton.B:
                ExitSolidColors();
                return;
            case GamepadButton.A when i == 14: // OK -> close
                ExitSolidColors();
                return;
            case GamepadButton.A when i == 0: // the hue-wheel tile opens the custom colour page
                EnterCustomColor();
                return;
            case GamepadButton.A when i < 14: // pick this colour: applied as the BACKGROUND instantly
                ApplySolidColor(BackgroundSettings.SolidPalette[i - 1]);
                return; // stay in the picker so the change is visible and others can be tried
            case GamepadButton.Left when i < 14 && i % 7 > 0:
                i--;
                break;
            case GamepadButton.Right when i < 14 && i % 7 < 6:
                i++;
                break;
            case GamepadButton.Up when i >= 7 && i < 14:
                i -= 7;
                break;
            case GamepadButton.Up when i == 14: // OK -> row 2, first column
                i = 7;
                break;
            case GamepadButton.Down when i < 7:
                i += 7;
                break;
            case GamepadButton.Down when i >= 7 && i < 14: // row 2 -> OK
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

    // Applies a solid colour as the HOME background LIVE and persists it. Stays in the picker (like
    // the accent picker) so another can be chosen. Note it only changes the home background; the
    // picker's own background (and the rest of Settings) is fixed and is NOT touched - "background"
    // only affects the home. Feedback inside the picker is the applied mark over the swatch; the
    // background change is seen on returning to the home.
    private void ApplySolidColor(string hex)
    {
        _backgroundSolidHex = hex;
        BackgroundSettings.SaveSolid(hex);
        ApplyBackground();           // HOME background + My background check
        RefreshSolidAppliedCheck();  // applied mark over the chosen swatch
    }

    // Builds the 14 "Solid colors" grid slots + their rings + the applied mark, inside
    // SolidSwatchHost. 7x2 grid, geometry measured 1:1 (see the SolidSwatch* constants). Slot 0 is
    // the custom colour tile, slots 1..13 the palette. Same pattern as BuildColorSwatches (the accent
    // picker).
    private void BuildSolidColorSwatches()
    {
        for (var i = 0; i < SolidSlotCount; i++)
        {
            var swatch = new Border
            {
                Width = SolidSwatchW,
                Height = SolidSwatchH,
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true,
            };

            if (i == 0)
            {
                BuildCustomColorTile(swatch);
            }
            else
            {
                swatch.Background = new SolidColorBrush(Color.Parse(BackgroundSettings.SolidPalette[i - 1]));
            }

            Canvas.SetLeft(swatch, SolidSwatchX(i));
            Canvas.SetTop(swatch, SolidSwatchY(i));
            SolidSwatchHost.Children.Add(swatch);
        }

        // Rings after the swatches so they render on top (their halo is not covered by the neighbour).
        for (var i = 0; i < SolidSlotCount; i++)
        {
            var ring = new Border { Width = SolidSwatchW + 16, Height = SolidSwatchH + 16 };
            ring.Classes.Add("selectionRing");
            Canvas.SetLeft(ring, SolidSwatchX(i) - 8);
            Canvas.SetTop(ring, SolidSwatchY(i) - 8);
            SolidSwatchHost.Children.Add(ring);
            _solidSwatchRings[i] = ring;
        }

        // "Applied" mark: white triangle (#EBEBEB) + dark check in the top right corner of the swatch
        // whose colour is the current background. Same as the accent picker's.
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

    // The custom colour tile (grid slot 0): a hue wheel washed out to white in the centre. Two layers
    // because it is exactly that - a conic hue sweep with a radial white overlay on top.
    //
    // Both were derived by sampling a 9x9 grid over the reference tile. The hue is a plain RGB sweep
    // through the six primaries, red at 12 o'clock, running clockwise. The white overlay fades
    // linearly, so saturation tracks the radius: measured 0.26 / 0.49 / 0.74 at a quarter, half and
    // three quarters of the way out, both across and down. Full saturation lands just outside the
    // tile, hence the 0.51 radii (0.5 would reach it exactly at the edge).
    //
    // The square-then-stretch is NOT cosmetic. The tile is 246x212, and a conic gradient drawn
    // straight onto it measures the angle in real pixels, which drags the hue off by ~6 degrees near
    // the corners (bottom left came out #002EFF against the reference's #0A42E7). The reference
    // computes the angle as if the tile were square, so the sweep is built on a square and stretched
    // to fit: that lands on #0040FF there, matching. The radial overlay stretches with it, which is
    // also what the reference does (its saturation ramp is identical across and down).
    private static void BuildCustomColorTile(Border host)
    {
        var square = new Panel
        {
            Width = 100,
            Height = 100,
            Background = new ConicGradientBrush
            {
                Center = RelativePoint.Center,
                GradientStops =
                {
                    new GradientStop(Color.Parse("#FF0000"), 0.0),
                    new GradientStop(Color.Parse("#FFFF00"), 1.0 / 6),
                    new GradientStop(Color.Parse("#00FF00"), 2.0 / 6),
                    new GradientStop(Color.Parse("#00FFFF"), 3.0 / 6),
                    new GradientStop(Color.Parse("#0000FF"), 4.0 / 6),
                    new GradientStop(Color.Parse("#FF00FF"), 5.0 / 6),
                    new GradientStop(Color.Parse("#FF0000"), 1.0),
                },
            },
        };

        square.Children.Add(new Avalonia.Controls.Shapes.Rectangle
        {
            Fill = new RadialGradientBrush
            {
                Center = RelativePoint.Center,
                GradientOrigin = RelativePoint.Center,
                RadiusX = new RelativeScalar(0.51, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.51, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#FFFFFFFF"), 0.0),
                    new GradientStop(Color.Parse("#00FFFFFF"), 1.0),
                },
            },
        });

        host.Child = new Viewbox { Stretch = Stretch.Fill, Child = square };
    }

    // Places the "applied" mark over the swatch of the current background, or hides it if the
    // background is the video (or a colour not in the palette).
    private void RefreshSolidAppliedCheck()
    {
        if (_solidAppliedCheck is null)
        {
            return;
        }

        var idx = SolidSlotOf(_backgroundSolidHex);
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

    // Grid slot (0..13) of a hex in the palette, or -1 when it is not one of the 13 - which is also
    // what a background that is a video, or a colour saved by an older build, lands on: the applied
    // mark is simply not shown, the background itself keeps working.
    private static int SolidSlotOf(string? hex)
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
                return i + 1; // slot 0 is the custom colour tile
            }
        }

        return -1;
    }

    // Entering/leaving "Solid colors - Custom". Focus starts on Hue, as the reference does. Closing
    // with B returns to Solid colors, which stays mounted behind exactly as it was.
    private void EnterCustomColor()
    {
        _customColorCards ??= new[] { CcHueCard, CcSatCard, CcLightCard, CcHexCard, CcSaveButton, CcMatchButton };
        _customColorRings ??= new[] { CcRing0, CcRing1, CcRing2, CcRing3, CcRing4, CcRing5 };

        _inCustomColor = true;
        _customColorIndex = 0;
        CustomColorUserName.Text = global::System.Environment.UserName;
        CustomColorScreen.IsVisible = true;
        UpdateCustomColorSelection();
    }

    private void ExitCustomColor()
    {
        _inCustomColor = false;
        CustomColorScreen.IsVisible = false;
    }

    // Left/Right only matter on the button row (SAVE / MATCH MY GAMERPIC); on the sliders they will
    // move the thumbs once this screen is wired up, so they are swallowed rather than falling through
    // to something else.
    private void MoveCustomColor(GamepadButton button)
    {
        var i = _customColorIndex;
        switch (button)
        {
            case GamepadButton.B:
                ExitCustomColor();
                return;
            case GamepadButton.Down when i < 3:
                i++;
                break;
            case GamepadButton.Down when i == 3:
                i = 4;
                break;
            case GamepadButton.Up when i == 4 || i == 5:
                i = 3;
                break;
            case GamepadButton.Up when i > 0:
                i--;
                break;
            case GamepadButton.Right when i == 4:
                i = 5;
                break;
            case GamepadButton.Left when i == 5:
                i = 4;
                break;
            default:
                return; // includes A: nothing here does anything yet
        }

        _customColorIndex = i;
        UpdateCustomColorSelection();
    }

    private void UpdateCustomColorSelection()
    {
        if (_customColorCards is null || _customColorRings is null)
        {
            return;
        }

        for (var i = 0; i < _customColorCards.Length; i++)
        {
            var selected = i == _customColorIndex;
            _customColorCards[i].Classes.Set("selected", selected);
            _customColorRings[i].Classes.Set("selected", selected);
        }
    }

    // Entering/leaving "Custom image" ("Where do you want to start?"; see CustomImageScreen in the
    // XAML). VISUAL ONLY: the four source cards take the ring but A does nothing. Focus starts on the
    // first one, as the reference does.
    private void EnterCustomImage()
    {
        _customImageCards ??= new[] { CiCard0, CiCard1, CiCard2, CiCard3 };
        _customImageRings ??= new[] { CiRing0, CiRing1, CiRing2, CiRing3 };

        _inCustomImage = true;
        _customImageIndex = 0;
        CustomImageScreen.IsVisible = true;
        UpdateCustomImageSelection();
    }

    private void ExitCustomImage()
    {
        _inCustomImage = false;
        CustomImageScreen.IsVisible = false;
    }

    private void MoveCustomImage(GamepadButton button)
    {
        var i = _customImageIndex;
        switch (button)
        {
            case GamepadButton.B:
                ExitCustomImage();
                return;
            case GamepadButton.Up when i > 0:
                i--;
                break;
            case GamepadButton.Down when i < 3:
                i++;
                break;
            default:
                return; // includes A: none of the four sources exists yet
        }

        _customImageIndex = i;
        UpdateCustomImageSelection();
    }

    private void UpdateCustomImageSelection()
    {
        if (_customImageCards is null || _customImageRings is null)
        {
            return;
        }

        for (var i = 0; i < _customImageCards.Length; i++)
        {
            var selected = i == _customImageIndex;
            _customImageCards[i].Classes.Set("selected", selected);
            _customImageRings[i].Classes.Set("selected", selected);
        }
    }

    // Entering/leaving "Dynamic backgrounds". Starts on the thumbnail row (focus 1), Games tab,
    // first thumbnail. Closing with B returns to My background.
    private void EnterDynamic()
    {
        _inDynamic = true;
        _dynTab = 0;
        _dynIndex = 0;
        _dynFocus = 1;
        _dynPreviewTargetVideo = ""; // forces UpdateDynPreview to apply the first one's preview
        _gamepad.RepeatEnabled = true; // wanted here: holding left/right accelerates through backgrounds
        DynamicBackgroundsScreen.IsVisible = true;
        BuildDynRail();
        UpdateDynamic();
    }

    private void ExitDynamic()
    {
        _inDynamic = false;
        _gamepad.RepeatEnabled = false; // outside Dynamic, no auto-repeat (like every other screen)
        DynamicBackgroundsScreen.IsVisible = false;
        _dynPreviewTimer?.Stop();
        // Destroy the SINGLE preview player (once, on exit; no longer on every background change).
        if (_dynPreviewVideo != null)
        {
            _dynPreviewVideo.VideoReady -= OnDynPreviewReady;
            DynPreviewHost.Children.Remove(_dynPreviewVideo);
            _dynPreviewVideo = null;
        }
        DynPreviewPoster.IsVisible = false;
        DynPreviewHost.Opacity = 0; // leave the container invisible for the next entry
        _dynPreviewTargetVideo = "";
    }

    // Applies a dynamic background (video) as the HOME background LIVE and persists it. Stays on the
    // screen (like the colour pickers) so another can be chosen; the "applied" mark moves to the
    // chosen thumbnail. Returning to the home with B shows the new video.
    private void ApplyDynamicBackground(DynBackground entry)
    {
        _backgroundSolidHex = null;
        _backgroundVideoRelPath = entry.VideoRelPath;
        BackgroundSettings.SaveVideo(entry.VideoRelPath);
        ApplyBackground();  // swaps the home video + puts My background's check on Dynamic
        BuildDynRail();     // repaints the thumbnails to move the "applied" mark
        UpdateDynamic();    // reapplies the rail offset and refreshes the label
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
            // Focus on the tabs: Left/Right changes tab (and rebuilds the rail); Down or A drops to
            // the thumbnails.
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
            // Focus on the thumbnails: Left/Right moves the selection; Up goes to the tabs.
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
                    // Applies the selected background (if that thumbnail already has a real one;
                    // placeholders still do nothing).
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

    // (Re)builds the current tab's thumbnails inside DynRailHost, placed at x = i*pitch. The rail
    // offset is set by UpdateDynamic via RenderTransform.
    private void BuildDynRail()
    {
        DynRailHost.Children.Clear();
        var count = DynTabCounts[_dynTab];
        // The background video applied right now (the chosen one, or the default): its thumbnail
        // carries the check mark.
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
                // Real thumbnail: its poster, but decoded SMALL (thumbnail size, not 1080p). Loading
                // dozens of full-resolution posters at once blew up the page (memory + composition).
                // The large poster (LoadPoster) is only used full screen and one at a time
                // (home / preview).
                if (LoadThumbnail(entry.PosterRelPath) is { } thumb)
                {
                    tile.Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };
                }

                // "Applied" mark if this thumbnail is precisely the home's active background.
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

    // Loads (with caching) the FULL RESOLUTION poster, to show it full screen (home background). Used
    // one at a time; null if the file does not exist.
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

    // Keeps ONLY the poster currently on screen in the home cached; releases the rest. Called right
    // AFTER setting HomeBackgroundPoster.Source to the new one, so "the one on screen" is already the
    // new one and the previous can be released. Disposal is deferred (see ScheduleDispose).
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

    // Frees a poster a moment LATER (not immediately): the image just removed from screen may still
    // be in flight on the GPU for ~1 frame, and freeing it right then crashes. 300 ms (some 20-30
    // frames) is plenty for it to be out of use.
    private void ScheduleDispose(Avalonia.Media.Imaging.Bitmap bmp)
        => DispatcherTimer.RunOnce(bmp.Dispose, TimeSpan.FromMilliseconds(300));

    // Cache of SMALL thumbnails (~320px versions of the poster) for the rail. Separate from the large
    // poster cache: the rail holds dozens of thumbnails and loading them at 1080p at once blew up the
    // page.
    private readonly global::System.Collections.Generic.Dictionary<string, Avalonia.Media.Imaging.Bitmap> _dynThumbCache = new();

    // Loads (with caching) a poster's thumbnail decoded to ~320px wide (the tile is 262px). Decoding
    // small is ~30x less memory and much faster than the full 1080p.
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

    // "Applied" mark of the current background, traced from Xbox: the WHOLE thumbnail is darkened
    // with a veil + a centred white check (thin stroke, rounded caps). Fills the tile.
    private static Control BuildDynAppliedBadge()
    {
        var host = new Grid();

        // Dark veil over the whole thumbnail (rounded the same as the tile).
        host.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#80000000")),
            CornerRadius = new CornerRadius(9),
        });

        // Centred white check.
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
        // Active tab (white + bold) + underline beneath it.
        for (var i = 0; i < _dynTabs.Length; i++)
        {
            _dynTabs[i].Classes.Set("active", i == _dynTab);
        }

        // The underline (beneath the active tab) and the background name are positioned from their
        // REAL WIDTH, which is only known after the layout pass (bold changes the tab's width; each
        // background has a name of a different width). Hence they are repositioned with Post
        // (Loaded priority = after measure/arrange).
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTabUnderline();
            UpdateDynLabelPosition();
        }, DispatcherPriority.Loaded);

        // Carousel: the rail is offset so the selected thumbnail lands on DynRailSelX.
        DynRailHost.RenderTransform = TransformOperations.Parse(
            $"translateX({(DynRailSelX - _dynIndex * DynThumbPitch).ToString(CultureInfo.InvariantCulture)}px)");

        // The (fixed) laser ring is only visible while focus is on the thumbnails; moving up to the
        // tabs hides it, which is how you can tell where focus is.
        DynRailRing.IsVisible = _dynFocus == 1;

        // Background name: the real one if that thumbnail already has a background, the placeholder
        // otherwise.
        DynLabel.Text = DynEntry(_dynTab, _dynIndex)?.Name ?? $"Background {_dynIndex + 1}";
        DynHintAction.Text = DynHintActions[_dynTab];

        // Full-screen preview of the focused background (even if it is not applied).
        UpdateDynPreview();
    }

    // Points the background preview at the wallpaper FOCUSED right now (_dynTab/_dynIndex): sets its
    // poster instantly and schedules its video after a settle delay. If the focused thumbnail does
    // not change wallpaper (e.g. moving focus between tabs and rail), it does nothing. For a
    // placeholder thumbnail (no real background) it leaves the dark base.
    private void UpdateDynPreview()
    {
        var entry = DynEntry(_dynTab, _dynIndex);
        var wantVideo = entry != null ? BackgroundFullPath(entry.VideoRelPath) : null;

        if (wantVideo == _dynPreviewTargetVideo)
        {
            return; // same wallpaper focused: nothing to change
        }
        _dynPreviewTargetVideo = wantVideo;

        // Loading image of the focused background (shown BLURRED), instantly and WITHOUT decoding
        // anything new: the small THUMBNAIL already cached by the rail. Under the blur it is
        // indistinguishable from a larger version, so no RAM or time goes into a separate mid-size
        // poster. The video fades to invisible meanwhile; it fades back in when its first frame is
        // ready (OnDynPreviewReady). No thumbnail (placeholder tab) -> dark base.
        if (entry != null && LoadThumbnail(entry.PosterRelPath) is { } quick)
        {
            DynPreviewPoster.Source = quick;
            DynPreviewPoster.IsVisible = true;
        }
        else
        {
            DynPreviewPoster.IsVisible = false;
        }
        // Fade the video to invisible: while the new one loads, the (blurred) poster underneath shows.
        // It fades back to 1 when the new first frame is ready (OnDynPreviewReady).
        DynPreviewHost.Opacity = 0;

        // Swap the preview video after a short settle delay (so it does not change on every step when
        // scrubbing quickly). It does not destroy/create the player: it only re-sources it (see the
        // Tick).
        _dynPreviewTimer?.Stop();
        if (wantVideo != null)
        {
            _dynPreviewTimer?.Start();
        }
    }

    // Creates the SINGLE preview player the first time it is needed (with whichever video applies at
    // that moment). It starts hidden; OnDynPreviewReady reveals it once its first frame is visible.
    private void EnsureDynPreviewControl()
    {
        if (_dynPreviewVideo != null)
        {
            return;
        }

        // The video itself is always visible; what controls whether it shows (and the fade) is the
        // opacity of DynPreviewHost, which wraps this control. The host starts at Opacity=0.
        _dynPreviewVideo = new HardwareVideoBackgroundControl(_dynPreviewTargetVideo!)
        {
            Width = 1920,
            Height = 1080,
        };
        _dynPreviewVideo.VideoReady += OnDynPreviewReady;
        DynPreviewHost.Children.Add(_dynPreviewVideo);
    }

    // The player signals (VideoReady) that the current video's first frame is visible: reveal it
    // (over the poster). Only if we are still on the screen and a video is still focused.
    private void OnDynPreviewReady()
    {
        if (_inDynamic && _dynPreviewVideo != null && _dynPreviewTargetVideo != null)
        {
            // Fade the sharp video in over the blurred poster ("blurry -> sharp").
            DynPreviewHost.Opacity = 1;

            // Once the crossfade is done the blurred poster is fully covered by the opaque video:
            // hide it so Avalonia does not keep re-blurring a full-screen image every frame behind
            // the video (Avalonia does not cull what is hidden behind something). Only if the
            // background has not changed meanwhile (the focused video is rechecked).
            var settledVideo = _dynPreviewTargetVideo;
            DispatcherTimer.RunOnce(() =>
            {
                if (_inDynamic && _dynPreviewTargetVideo == settledVideo && DynPreviewHost.Opacity >= 1)
                    DynPreviewPoster.IsVisible = false;
            }, TimeSpan.FromMilliseconds(240));
        }
    }

    // Places the underline beneath the active tab's word at its REAL WIDTH (Bounds after layout,
    // which already reflects the bold) and aligned with it. The pill shape (Height 6, radius 3 in the
    // XAML) gives it the fully rounded caps of the reference.
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

    // Horizontal centre of the selected thumbnail (pinned at DynRailSelX, width 262 from the
    // dynThumb style): the background name is centred here.
    private const double DynThumbSelCenterX = DynRailSelX + 131; // 131 (left edge) + 262/2

    // Centres the background name over the selected thumbnail (its real width is only known after
    // layout, and changes with each name).
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

        // The top dark gradient is only needed while focus is on that row (avatar/nav) - on the games
        // below the background is left as is, undarkened.
        TopBarGradient.Opacity = _row == 0 ? 1 : 0;

        // The text under the navigation cluster is ONLY visible while focus is on that row (row 0).
        // Moving down to the games fades it out (Opacity 0) rather than leaving the last text on
        // screen - on a real Xbox it does not persist over the games either. Text and position are
        // only updated on row 0; leaving it keeps the last text but invisible, so it does not flicker
        // while fading out.
        if (_row == 0)
        {
            NavLabel.Text = NavLabels[_col];
            Canvas.SetLeft(NavLabel, _rowCenters[0][_col] - NavLabel.Width / 2);
        }

        NavLabel.Opacity = _row == 0 ? 1 : 0;
    }

    // How much the selected tile grows, per row.
    //
    // Row 1 (games): 1.618 - the golden ratio, and that is no coincidence. Measured on 5 real Xbox
    // home captures with different games selected: the 4 that could be measured give 1.6127, 1.6179,
    // 1.6188 and 1.6220. The earlier value (1.23) was a fudge: it had been chosen as "the most that
    // fits without touching the neighbouring tile" because we then believed the neighbours did not
    // move. They do.
    //
    // Row 2 (the 4 wide tiles): 1.12, inherited. NOT measured - the references only cover the games
    // row. At 1.618 that row would not fit on screen, so until there is a reference it stays as it
    // was.
    private static readonly double[] SelectedTileScale = { 0, 1.618, 1.12 };

    // Base (unselected) size of each row's tiles, exactly as declared in the XAML. This has to be
    // stored because selection changes Width/Height for real (it is not a transform), so deselecting
    // needs to know what to return to.
    private static readonly double[] BaseTileWidth = { 0, 154, 400 };
    private static readonly double[] BaseTileHeight = { 0, 154, 230 };

    // How far the selection ring extends beyond its tile, per side. Re-measured against the real
    // video: the Xbox bright stroke is SEPARATED from the tile by a small gap (~4.5px) filled with
    // the dark tone of the glow. With the thin stroke (2.5) and an inflation of 7, the stroke's inner
    // edge lands ~4.5px from the tile -> that is the gap, which the dark-green "inset" shadow fills.
    // Even on purpose (symmetry under UseLayoutRounding).
    private const double RingInflate = 8;

    // Positions the home tiles according to the selection, replicating what Xbox does. The geometry
    // of the SELECTED state was measured on a video of the real home; the AT REST state (nothing
    // selected) never appears in that video and was measured on a separate capture.
    //
    // Two states per row:
    //   - AT REST (this row does not have focus): tiles are spread to FILL the width, with the ends
    //     at 110 and 1824 (aligned with the row below). Those are their XAML positions, so at rest
    //     translateX = 0. The gap between tiles is the "large" one (41).
    //   - SELECTED: the chosen tile grows (golden ratio) anchored at its BOTTOM LEFT corner (free of
    //     charge: they use Canvas.Left + Canvas.Bottom, so raising the size grows it up and right).
    //     The rest are SQUEEZED towards it (the gap drops to ~29) while pinning BOTH ends at 110 and
    //     1824. They neither bunch up at the start nor overflow.
    //
    // The offset of each tile i from its at-rest position, with tile 'sel' selected, is:
    //   translateX(i) = -i*growth/(n-1) + (i > sel ? growth : 0)
    // Both ends give 0 (i=0 -> 0; i=n-1 -> -growth + growth = 0), i.e. they do not move. Verified
    // against the video: it reproduces the 9 tile positions to within 1px.
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

                // The ring is positioned from the tile plus RingInflate (a single source of truth),
                // rather than from fixed Canvas.Left/Bottom in the XAML kept in sync by hand.
                // Canvas.GetLeft/GetBottom give the tile's BASE position (the sideways push is a
                // RenderTransform that does not touch these attached properties) and the ring
                // inherits the same translateX below, so it stays centred over its tile as it grows.
                Canvas.SetLeft(ringRow[c], Canvas.GetLeft(row[c]) - RingInflate);
                Canvas.SetBottom(ringRow[c], Canvas.GetBottom(row[c]) - RingInflate);

                // At rest (row without focus) they all stay at their XAML position (spread out).
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
