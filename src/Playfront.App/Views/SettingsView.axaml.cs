using System;
using System.Globalization;
using Playfront.App.Input;
using Avalonia.Controls;
using Avalonia.Media.Transformation;

namespace Playfront.App.Views;

/// <summary>
/// The Settings screen. It lives in its own view so it can be loaded ON DEMAND: MainWindow creates it
/// on entry and releases it on exit, so it is never resident. Nothing is built before it is opened.
///
/// It owns its navigation (category list plus one card grid per category). Anything that LEAVES this
/// screen is raised as an event, so the view never depends on MainWindow:
/// <see cref="PersonalizationRequested"/> and <see cref="ExitRequested"/>.
/// </summary>
public partial class SettingsView : UserControl
{
    // Canvas.Top of each SNavX in the XAML (index = category), used to slide the highlight by
    // translateY relative to NavTops[1], the base position of "General".
    private static readonly double[] NavTops = { 153, 216, 280, 344, 408, 472, 536 };

    private static readonly string[] CategoryNames =
    {
        "Recommendations", "General", "Account", "System", "Devices & connections", "Preferences", "Accessibility",
    };

    private const int GeneralCategory = 1;
    private const int SystemCategory = 3;

    private readonly Border[] _navItems;

    // One grid per category, indexed like CategoryNames; null means the category has no content yet
    // and stays a navigable placeholder in the list.
    //
    // These are JAGGED arrays on purpose: each row declares its own columns, which is what lets the
    // last row of System hold a single card ("Time") with no special case in the navigation — reading
    // the length of the current row is enough.
    private readonly Border[][]?[] _grids;
    private readonly Border[][]?[] _rings;

    private int _category = GeneralCategory;
    private bool _inGrid;
    private int _gridRow;
    private int _gridCol;

    /// <summary>Requests opening Personalization (the "Personalization" card in General).</summary>
    public event Action? PersonalizationRequested;

    /// <summary>Requests opening "System Updates" (the "Updates" card in System).</summary>
    public event Action? UpdatesRequested;

    /// <summary>Requests closing Settings and returning home (B on the category list).</summary>
    public event Action? ExitRequested;

    public SettingsView()
    {
        InitializeComponent();

        _navItems = new[] { SNav0, SNav1, SNav2, SNav3, SNav4, SNav5, SNav6 };

        _grids = new Border[][]?[CategoryNames.Length];
        _rings = new Border[][]?[CategoryNames.Length];

        _grids[GeneralCategory] = new[]
        {
            new[] { SCard0, SCard1 },
            new[] { SCard2, SCard3 },
            new[] { SCard4, SCard5 },
        };
        _rings[GeneralCategory] = new[]
        {
            new[] { SRing0, SRing1 },
            new[] { SRing2, SRing3 },
            new[] { SRing4, SRing5 },
        };

        _grids[SystemCategory] = new[]
        {
            new[] { SysCard0, SysCard1 },
            new[] { SysCard2, SysCard3 },
            new[] { SysCard4, SysCard5 },
            new[] { SysCard6 },
        };
        _rings[SystemCategory] = new[]
        {
            new[] { SysRing0, SysRing1 },
            new[] { SysRing2, SysRing3 },
            new[] { SysRing4, SysRing5 },
            new[] { SysRing6 },
        };

        SettingsUserNameText.Text = global::System.Environment.UserName;
        UpdateSelection();
    }

    private Border[][]? CurrentGrid => _grids[_category];

    public void Move(GamepadButton button)
    {
        var grid = CurrentGrid;

        switch (button)
        {
            // "Personalization" is row 0, column 1 of the General grid (SCard1 in the XAML) — the only
            // card that opens its own screen so far. The category check is essential: without it, that
            // same position in System ("Storage devices") would open Personalization.
            case GamepadButton.A when _inGrid && _category == GeneralCategory && _gridRow == 0 && _gridCol == 1:
                PersonalizationRequested?.Invoke();
                return;

            // "Updates" is row 1, column 0 of the System grid (SysCard2).
            case GamepadButton.A when _inGrid && _category == SystemCategory && _gridRow == 1 && _gridCol == 0:
                UpdatesRequested?.Invoke();
                return;

            case GamepadButton.B when _inGrid:
                _inGrid = false;
                break;
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;

            // The grid is entered with A or Right, since the cards sit to the right of the list;
            // Left on the first column goes back to it.
            case GamepadButton.A when !_inGrid && grid != null:
            case GamepadButton.Right when !_inGrid && grid != null:
                _inGrid = true;
                _gridRow = 0;
                _gridCol = 0;
                break;

            case GamepadButton.Up when _inGrid && _gridRow > 0:
                _gridRow--;
                ClampColumn();
                break;
            case GamepadButton.Down when _inGrid && grid != null && _gridRow < grid.Length - 1:
                _gridRow++;
                ClampColumn();
                break;
            case GamepadButton.Left when _inGrid && _gridCol > 0:
                _gridCol--;
                break;
            case GamepadButton.Left when _inGrid:
                _inGrid = false;
                break;
            case GamepadButton.Right when _inGrid && grid != null && _gridCol < grid[_gridRow].Length - 1:
                _gridCol++;
                break;

            case GamepadButton.Up when !_inGrid && _category > 0:
                _category--;
                break;
            case GamepadButton.Down when !_inGrid && _category < _navItems.Length - 1:
                _category++;
                break;

            default:
                return;
        }

        UpdateSelection();
    }

    // Moving rows can land on one with fewer columns (System's last row holds a single card). Without
    // this, going down from "Backup & transfer" would leave the selection on a column that does not
    // exist and nothing would appear selected.
    private void ClampColumn()
    {
        var grid = CurrentGrid;
        if (grid == null) return;

        var lastColumn = grid[_gridRow].Length - 1;
        if (_gridCol > lastColumn) _gridCol = lastColumn;
    }

    private void UpdateSelection()
    {
        // Two shared rectangles slide to the category's row via RenderTransform, sharing the same
        // offset and transition: SettingsNavHighlight (solid accent block, while browsing the list)
        // and SettingsCategoryIndicator (grey strip with an accent bar, while focus is in the grid).
        var offsetY = NavTops[_category] - NavTops[GeneralCategory];
        var offsetTransform = TransformOperations.Parse($"translateY({offsetY.ToString(CultureInfo.InvariantCulture)}px)");

        SettingsNavHighlight.IsVisible = !_inGrid;
        SettingsNavHighlight.RenderTransform = offsetTransform;

        SettingsCategoryIndicator.IsVisible = _inGrid;
        SettingsCategoryIndicator.RenderTransform = offsetTransform;

        SettingsCategoryTitle.Text = CategoryNames[_category];

        // Every grid is walked, not just the current one: that is what turns off the category just
        // left behind. Touching only the current one would leave General's cards drawn underneath
        // System's.
        for (var category = 0; category < _grids.Length; category++)
        {
            var grid = _grids[category];
            var rings = _rings[category];
            if (grid == null || rings == null) continue;

            var visible = category == _category;
            for (var r = 0; r < grid.Length; r++)
            {
                for (var c = 0; c < grid[r].Length; c++)
                {
                    var isSelected = visible && _inGrid && r == _gridRow && c == _gridCol;
                    grid[r][c].IsVisible = visible;
                    grid[r][c].Classes.Set("selected", isSelected);
                    // The ring hides with its card: otherwise switching to a category with no grid
                    // would leave an accent rectangle floating over empty space.
                    rings[r][c].IsVisible = visible;
                    rings[r][c].Classes.Set("selected", isSelected);
                }
            }
        }
    }
}
