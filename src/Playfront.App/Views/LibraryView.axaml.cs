using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Playfront.App.Library;
using Playfront.App.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Playfront.App.Views;

// "My games & apps" (library). Recreated 1:1 from the reference capture; controller-navigable.
//
// Two focus states:
//   - MENU: focus in the category column. The focused category gets the SOLID accent highlight
//     (LMenuHighlight). Moving up/down switches the content on the right (title + panel) to that
//     category: Games -> game grid, Manage -> management cards. The other four (Apps/Groups/Game
//     history/Full library) have no design yet and show only their title.
//   - CONTENT: focus inside the content. The active category switches to the "open" indicator (grey
//     strip + accent bar, LCategoryIndicator) and the focused item (game or card) gets the ring.
//
// A/Right on a category WITH content (Games or Manage) enters it; B or Left from column 0 returns to
// the menu; B in the menu exits to Home.
public partial class LibraryView : UserControl
{
    // The window listens to this to go back Home.
    public event Action? ExitRequested;

    private enum FocusArea { Menu, Content }

    private FocusArea _focus = FocusArea.Menu;
    private int _menuIndex;      // 0=Games,1=Apps,2=Groups,3=Play history,4=Full library,5=Manage
    private int _contentIndex;   // within the active category: game (Games 0..9) or card (Manage 0..8)

    private const int GamesCat = 0;
    private const int AppsCat = 1;
    private const int ManageCat = 5;

    // Display title of each category.
    private static readonly string[] CategoryNames = { "Games", "Apps", "Groups", "Play history", "Full library", "Manage" };

    // Canvas.Top of each menu category (the highlight/indicator slides to these). The first five are
    // contiguous; "Manage" sits lower because of the divider.
    private static readonly double[] MenuRowTops = { 181, 255, 328, 402, 475, 592 };

    // Games grid: 6 tiles on the top row, 4 on the bottom.
    private static readonly int[] GridRowLengths = { 6, 4 };

    // Manage cards by row (2 columns; the last row has only the left one).
    private static readonly int[][] ManageGrid =
    {
        new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 }, new[] { 8 },
    };

    private Border[] _rings = null!;        // game rings (Games)
    private Border[] _labels = null!;       // game name labels (Games)
    private Border[] _manageRings = null!;  // card rings (Manage)

    // The grid's ten slots. The layout is fixed (every coordinate is measured against the console),
    // so the library fills the first N slots and the rest are hidden. Ten is the ceiling for now;
    // paging comes with the providers, when there can be more than ten of anything.
    private Control[] _tiles = null!;
    private Image[] _arts = null!;
    private TextBlock[] _names = null!;

    /// <summary>What is on the grid right now: the library entries of the active category.</summary>
    private List<LibraryEntry> _shown = new();

    public LibraryView()
    {
        InitializeComponent();

        // The real account name (same as Home/Settings), not the one from the reference capture.
        LibraryUserName.Text = Environment.UserName;

        _rings = new[] { LRing0, LRing1, LRing2, LRing3, LRing4, LRing5, LRing6, LRing7, LRing8, LRing9 };
        _labels = new[] { LLabel0, LLabel1, LLabel2, LLabel3, LLabel4, LLabel5, LLabel6, LLabel7, LLabel8, LLabel9 };
        _manageRings = new[] { MRing0, MRing1, MRing2, MRing3, MRing4, MRing5, MRing6, MRing7, MRing8 };

        _tiles = new Control[] { LTile0, LTile1, LTile2, LTile3, LTile4, LTile5, LTile6, LTile7, LTile8, LTile9 };
        _arts = new[] { LArt0, LArt1, LArt2, LArt3, LArt4, LArt5, LArt6, LArt7, LArt8, LArt9 };
        _names = new[] { LName0, LName1, LName2, LName3, LName4, LName5, LName6, LName7, LName8, LName9 };

        UpdateStorage();
        ReloadLibrary();
        UpdateVisuals();
    }

    // How many navigable items the active category has (0 when it has no content).
    private int ContentCount => _menuIndex == ManageCat ? _manageRings.Length
        : HasGrid(_menuIndex) ? _shown.Count : 0;

    /// <summary>Which categories draw the tile grid. Games and Apps share it; the console's look the same.</summary>
    private static bool HasGrid(int cat) => cat == GamesCat || cat == AppsCat;

    /// <summary>
    /// Rebuilds the grid from the user's library for the active category. Called when the view opens
    /// and whenever the category changes.
    ///
    /// The category is NOT guessed from the title: every entry carries where it came from, and that
    /// decides whether it is a game or an app. That is what the console gets for free from its store
    /// and what Playfront has to keep for itself on Windows.
    /// </summary>
    private void ReloadLibrary()
    {
        _shown = HasGrid(_menuIndex)
            ? LibraryStore.OfKind(_menuIndex == GamesCat ? LibraryKind.Game : LibraryKind.App).ToList()
            : new List<LibraryEntry>();

        for (var i = 0; i < _tiles.Length; i++)
        {
            var hay = i < _shown.Count;
            _tiles[i].IsVisible = hay;
            if (!hay)
            {
                continue;
            }

            var e = _shown[i];
            _names[i].Text = e.Title;
            _arts[i].Source = LoadStoreArt(e.Art);
        }

        if (_contentIndex >= _shown.Count)
        {
            _contentIndex = Math.Max(0, _shown.Count - 1);
        }

        // Real counts, from the same list the grid shows, so the number and the tiles cannot
        // disagree. An empty category shows nothing rather than a zero.
        var juegos = LibraryStore.OfKind(LibraryKind.Game).Count();
        var apps = LibraryStore.OfKind(LibraryKind.App).Count();
        LCount0.Text = juegos > 0 ? juegos.ToString(CultureInfo.InvariantCulture) : "";
        LCount1.Text = apps > 0 ? apps.ToString(CultureInfo.InvariantCulture) : "";
        LCount2.Text = "";
        LGamesCount.Text = _shown.Count == 1
            ? "1 " + (_menuIndex == GamesCat ? "game" : "app")
            : _shown.Count + " " + (_menuIndex == GamesCat ? "games" : "apps");

        // Nothing in this category yet: say so instead of leaving an unexplained blank.
        LEmptyText.IsVisible = HasGrid(_menuIndex) && _shown.Count == 0;
        LEmptyText.Text = _menuIndex == GamesCat
            ? "No games in your library yet. Add some from the Store."
            : "No apps in your library yet. Add some from the Store.";
    }

    private static Bitmap? LoadStoreArt(string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return null;
        }

        try
        {
            return new Bitmap(AssetLoader.Open(new Uri($"avares://Playfront.App/Assets/Icons/Store/Apps/{file}")));
        }
        catch
        {
            return null; // no art: the tile stays as the plain placeholder rather than breaking
        }
    }

    // ===== Storage meter (bottom left) =====
    // Ring geometry, matching the XAML: stroke centre and radius.
    // Centre and radius of the storage ring, measured on the console: outer diameter 96 with a
    // 4 px stroke, so the centreline radius is (96-4)/2 = 46. Must match the Ellipse in the XAML.
    private const double RingCx = 327, RingCy = 968, RingR = 46;

    // Fills "available" (free GB), the centre percentage and the accent arc from the REAL free space on
    // the system drive. The arc covers the free percentage; the remainder shows the track underneath.
    // If the drive cannot be read, whatever the XAML holds is left alone.
    private void UpdateStorage()
    {
        try
        {
            var sysRoot = Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";
            var drive = new DriveInfo(sysRoot);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return;
            }

            double free = drive.AvailableFreeSpace;
            double total = drive.TotalSize;
            var percentFree = free / total * 100.0;
            // "Windows GB" = base 1024, matching what Explorer shows, not base 10.
            var freeGiB = free / (1024.0 * 1024.0 * 1024.0);

            // "64.0 GB free" - the word is part of the line on the console, not a separate one.
            LStorageSize.Text = FormatSize(freeGiB) + " free";
            LStoragePercent.Text = percentFree.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            LStorageArc.Data = BuildRingArc(percentFree);
        }
        catch (Exception)
        {
            // Best-effort: if the drive cannot be read, the XAML placeholder stays.
        }
    }

    private static string FormatSize(double giB)
    {
        if (giB >= 1024)
        {
            return (giB / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " TB";
        }

        // >=100 GB with no decimals ("680 GB"); below that, one decimal ("59.8 GB").
        return giB.ToString(giB >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB";
    }

    // Builds the ring arc for a percentage (0..100): 0% is at the TOP (12 o'clock, as in the capture)
    // and the stroke fills CLOCKWISE by that percentage; the gap is left of top.
    private static Geometry BuildRingArc(double percent)
    {
        percent = Math.Clamp(percent, 0.5, 99.9); // avoids degenerate arcs (exactly 0% or 100%)
        var sweep = percent / 100.0 * 360.0;
        var (x1, y1) = ClockPoint(0);      // start: straight up (0%)
        var (x2, y2) = ClockPoint(sweep);  // end: 'sweep' degrees clockwise
        var large = sweep > 180.0 ? 1 : 0;
        var inv = CultureInfo.InvariantCulture;
        var data = $"M{x1.ToString("0.##", inv)},{y1.ToString("0.##", inv)} " +
                   $"A{RingR.ToString(inv)},{RingR.ToString(inv)} 0 {large},1 " +
                   $"{x2.ToString("0.##", inv)},{y2.ToString("0.##", inv)}";
        return Geometry.Parse(data);
    }

    // Point on the ring at a clock angle: 0 = top, clockwise.
    private static (double x, double y) ClockPoint(double aDeg)
    {
        var rad = aDeg * Math.PI / 180.0;
        return (RingCx + RingR * Math.Sin(rad), RingCy - RingR * Math.Cos(rad));
    }

    // Controller routing from MainWindow.Move.
    public void Move(GamepadButton button)
    {
        if (_focus == FocusArea.Menu)
        {
            MoveMenu(button);
        }
        else
        {
            MoveContent(button);
        }
    }

    private void MoveMenu(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.Up when _menuIndex > 0:
                _menuIndex--;
                break;
            case GamepadButton.Down when _menuIndex < MenuRowTops.Length - 1:
                _menuIndex++;
                break;
            // Start does nothing while focus is on the menu: it removes an ENTRY, and there is none
            // focused here. Swallowed so it does not fall through to the default.
            case GamepadButton.Start:
                return;
            // Enter the highlighted category's content, if it has any (Games or Manage).
            case GamepadButton.A when ContentCount > 0:
            case GamepadButton.Right when ContentCount > 0:
                EnterContent();
                return;
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            default:
                return;
        }

        // The grid follows the highlighted category, so moving through the menu swaps its contents.
        ReloadLibrary();
        UpdateVisuals();
        AnimarEntradaContenido(button == GamepadButton.Down ? 1 : -1);
    }

    /// <summary>
    /// Plays the console's category-change animation: the new content appears offset in the
    /// direction of travel and almost transparent, then slides into place while fading in.
    ///
    /// The starting state has to be applied with the transitions SUSPENDED. Otherwise Avalonia
    /// animates the jump to the offset as well and what you see is the content swinging out and
    /// back, which is not what the console does - it only animates the way IN.
    /// </summary>
    private void AnimarEntradaContenido(int direccion)
    {
        // Travel extrapolated from the measured decay: the element is still 140 px out when it
        // first becomes visible, and the curve puts its origin around 190 px in the direction of
        // travel. Down through the menu -> content rises from below, up -> descends from above.
        const double Desplazamiento = 190;

        var trans = LContentAnim.Transitions;
        LContentAnim.Transitions = null;
        LContentAnim.RenderTransform = TransformOperations.Parse(
            $"translateY({(direccion * Desplazamiento).ToString(CultureInfo.InvariantCulture)}px)");
        LContentAnim.Opacity = 0;
        LContentAnim.Transitions = trans;

        // Next frame, or the two states collapse into one and nothing animates.
        Dispatcher.UIThread.Post(() =>
        {
            LContentAnim.RenderTransform = TransformOperations.Parse("translateY(0px)");
            LContentAnim.Opacity = 1;
        }, DispatcherPriority.Render);
    }

    // Inside the content: route by active category.
    private void MoveContent(GamepadButton button)
    {
        if (HasGrid(_menuIndex))
        {
            MoveGamesGrid(button);
        }
        else if (_menuIndex == ManageCat)
        {
            MoveManageGrid(button);
        }
    }

    /// <summary>Asks MainWindow to run the focused entry (it owns the screens an entry can open).</summary>
    public event Action<LibraryEntry>? LaunchRequested;

    /// <summary>The focused entry, or null when focus is not on a tile.</summary>
    private LibraryEntry? Focused
        => _focus == FocusArea.Content && HasGrid(_menuIndex) && _contentIndex < _shown.Count
            ? _shown[_contentIndex] : null;

    // GAMES grid navigation (6 on top + 4 below).
    private void MoveGamesGrid(GamepadButton button)
    {
        var row = _contentIndex < GridRowLengths[0] ? 0 : 1;
        var col = row == 0 ? _contentIndex : _contentIndex - GridRowLengths[0];

        switch (button)
        {
            // A runs the focused entry. If it is only OWNED it gets installed first; for a web app
            // there is nothing to fetch, so that step is immediate.
            case GamepadButton.A when Focused is not null:
                var entrada = Focused!;
                if (entrada.State == LibraryState.Owned)
                {
                    LibraryStore.SetState(entrada.Id, LibraryState.Installed);
                }
                LaunchRequested?.Invoke(entrada);
                return;
            // Start (the "hamburger"/Menu button) takes the entry OUT of the library, like the console's
            // "Remove". It only forgets it - nothing on disk is touched - and it can be added again
            // from the Store.
            case GamepadButton.Start when Focused is not null:
                LibraryStore.Remove(Focused!.Id);
                ReloadLibrary();
                if (_shown.Count == 0)
                {
                    ExitContent();
                    return;
                }
                break;
            case GamepadButton.Left when col > 0:
                _contentIndex--;
                break;
            // Left from the first column returns to the menu, as on Xbox.
            case GamepadButton.Left:
            case GamepadButton.B:
                ExitContent();
                return;
            case GamepadButton.Right when col < GridRowLengths[row] - 1 && _contentIndex + 1 < _shown.Count:
                _contentIndex++;
                break;
            case GamepadButton.Down when row == 0:
                _contentIndex = GridRowLengths[0] + Math.Min(col, GridRowLengths[1] - 1);
                break;
            case GamepadButton.Up when row == 1:
                _contentIndex = col;
                break;
            default:
                return;
        }

        UpdateVisuals();
    }

    // MANAGE cards navigation (2 columns; the last row has only the left one).
    private void MoveManageGrid(GamepadButton button)
    {
        var (row, col) = ManagePos(_contentIndex);

        switch (button)
        {
            case GamepadButton.Left when col > 0:
                _contentIndex = ManageGrid[row][col - 1];
                break;
            case GamepadButton.Left:
            case GamepadButton.B:
                ExitContent();
                return;
            case GamepadButton.Right when col < ManageGrid[row].Length - 1:
                _contentIndex = ManageGrid[row][col + 1];
                break;
            case GamepadButton.Up when row > 0:
                _contentIndex = ManageGrid[row - 1][Math.Min(col, ManageGrid[row - 1].Length - 1)];
                break;
            case GamepadButton.Down when row < ManageGrid.Length - 1:
                _contentIndex = ManageGrid[row + 1][Math.Min(col, ManageGrid[row + 1].Length - 1)];
                break;
            default:
                return;
        }

        UpdateVisuals();
    }

    // (row, column) of a Manage card from its index.
    private static (int row, int col) ManagePos(int card)
    {
        for (var r = 0; r < ManageGrid.Length; r++)
        {
            for (var c = 0; c < ManageGrid[r].Length; c++)
            {
                if (ManageGrid[r][c] == card)
                {
                    return (r, c);
                }
            }
        }

        return (0, 0);
    }

    // Enter the active category's content, selecting the first item.
    private void EnterContent()
    {
        _focus = FocusArea.Content;
        _contentIndex = 0;
        UpdateVisuals();
    }

    // Back to the menu (the active category stays highlighted).
    private void ExitContent()
    {
        _focus = FocusArea.Menu;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var inContent = _focus == FocusArea.Content;

        // Right-hand content follows the active category (Games / Manage / empty), title included.
        LContentTitle.Text = CategoryNames[_menuIndex];
        LGamesContent.IsVisible = HasGrid(_menuIndex);
        LManageContent.IsVisible = _menuIndex == ManageCat;

        // Solid accent highlight (focus in menu) vs the "category open" indicator (focus in content).
        // Both sit on the active category's row.
        var shift = TransformOperations.Parse($"translateY({MenuRowTops[_menuIndex] - MenuRowTops[0]}px)");
        LMenuHighlight.IsVisible = !inContent;
        LMenuHighlight.RenderTransform = shift;
        LCategoryIndicator.IsVisible = inContent;
        LCategoryIndicator.RenderTransform = shift;

        // The count on the highlighted row turns WHITE. In grey it is nearly unreadable against the
        // solid accent, and the console does the same. Only while focus is on the menu: with the
        // category open the band is grey and the count stays grey.
        var cuentas = new[] { LCount0, LCount1, LCount2 };
        for (var i = 0; i < cuentas.Length; i++)
        {
            cuentas[i].Foreground = !inContent && i == _menuIndex
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        }

        // GAME rings and labels: only the focused item, and only with content focus in Games.
        var gamesFocus = inContent && HasGrid(_menuIndex);
        for (var i = 0; i < _rings.Length; i++)
        {
            var on = gamesFocus && i == _contentIndex && i < _shown.Count;
            SetClass(_rings[i], "selected", on);
            _labels[i].IsVisible = on;
        }

        // Manage CARD rings: only the focused one, and only with content focus in Manage.
        var manageFocus = inContent && _menuIndex == ManageCat;
        for (var i = 0; i < _manageRings.Length; i++)
        {
            SetClass(_manageRings[i], "selected", manageFocus && i == _contentIndex);
        }

        // Bottom-right hints: the game actions (Manage game / More options) only with a game focused;
        // in any other state, "Search".
        LGridHints.IsVisible = gamesFocus;
        LSearchHint.IsVisible = !gamesFocus;
    }

    private static void SetClass(StyledElement element, string className, bool on)
    {
        if (on)
        {
            if (!element.Classes.Contains(className))
            {
                element.Classes.Add(className);
            }
        }
        else
        {
            element.Classes.Remove(className);
        }
    }
}
