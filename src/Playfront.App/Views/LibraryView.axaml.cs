using System;
using System.Globalization;
using System.IO;
using Playfront.App.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;

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
    private int _menuIndex;      // 0=Games,1=Apps,2=Groups,3=Game history,4=Full library,5=Manage
    private int _contentIndex;   // within the active category: game (Games 0..9) or card (Manage 0..8)

    private const int GamesCat = 0;
    private const int ManageCat = 5;

    // Display title of each category.
    private static readonly string[] CategoryNames = { "Games", "Apps", "Groups", "Game history", "Full library", "Manage" };

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

    public LibraryView()
    {
        InitializeComponent();

        // The real account name (same as Home/Settings), not the one from the reference capture.
        LibraryUserName.Text = Environment.UserName;

        _rings = new[] { LRing0, LRing1, LRing2, LRing3, LRing4, LRing5, LRing6, LRing7, LRing8, LRing9 };
        _labels = new[] { LLabel0, LLabel1, LLabel2, LLabel3, LLabel4, LLabel5, LLabel6, LLabel7, LLabel8, LLabel9 };
        _manageRings = new[] { MRing0, MRing1, MRing2, MRing3, MRing4, MRing5, MRing6, MRing7, MRing8 };

        UpdateStorage();
        UpdateVisuals();
    }

    // How many navigable items the active category has (0 when it has no content).
    private int ContentCount => _menuIndex == GamesCat ? _rings.Length
        : _menuIndex == ManageCat ? _manageRings.Length : 0;

    // ===== Storage meter (bottom left) =====
    // Ring geometry, matching the XAML: stroke centre and radius.
    private const double RingCx = 330, RingCy = 993, RingR = 42.5;

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

            LStorageSize.Text = FormatSize(freeGiB);
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

        UpdateVisuals();
    }

    // Inside the content: route by active category.
    private void MoveContent(GamepadButton button)
    {
        if (_menuIndex == GamesCat)
        {
            MoveGamesGrid(button);
        }
        else if (_menuIndex == ManageCat)
        {
            MoveManageGrid(button);
        }
    }

    // GAMES grid navigation (6 on top + 4 below).
    private void MoveGamesGrid(GamepadButton button)
    {
        var row = _contentIndex < GridRowLengths[0] ? 0 : 1;
        var col = row == 0 ? _contentIndex : _contentIndex - GridRowLengths[0];

        switch (button)
        {
            case GamepadButton.Left when col > 0:
                _contentIndex--;
                break;
            // Left from the first column returns to the menu, as on Xbox.
            case GamepadButton.Left:
            case GamepadButton.B:
                ExitContent();
                return;
            case GamepadButton.Right when col < GridRowLengths[row] - 1:
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
        LGamesContent.IsVisible = _menuIndex == GamesCat;
        LManageContent.IsVisible = _menuIndex == ManageCat;

        // Solid accent highlight (focus in menu) vs the "category open" indicator (focus in content).
        // Both sit on the active category's row.
        var shift = TransformOperations.Parse($"translateY({MenuRowTops[_menuIndex] - MenuRowTops[0]}px)");
        LMenuHighlight.IsVisible = !inContent;
        LMenuHighlight.RenderTransform = shift;
        LCategoryIndicator.IsVisible = inContent;
        LCategoryIndicator.RenderTransform = shift;

        // GAME rings and labels: only the focused item, and only with content focus in Games.
        var gamesFocus = inContent && _menuIndex == GamesCat;
        for (var i = 0; i < _rings.Length; i++)
        {
            var on = gamesFocus && i == _contentIndex;
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
