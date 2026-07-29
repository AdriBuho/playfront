using System;
using Playfront.App.Input;
using Avalonia.Controls;

namespace Playfront.App.Views;

/// <summary>
/// The "System Language &amp; location" screen (Settings -> System -> Language &amp; location).
/// Mounted on demand and released on B.
///
/// VISUAL ONLY: the five cards take the ring and move with the d-pad, but A does nothing. The values
/// shown are the reference's, not this machine's - the app has no localisation yet, so there is
/// nothing behind them to read or write.
/// </summary>
public partial class SystemLanguageView : UserControl
{
    // Column 0 holds the three System cards, column 1 the Input card, column 2 "Restart now". Moving
    // sideways into a one-card column clamps to its only row, the same rule Personalization uses.
    private static readonly int[] ColumnHeights = { 3, 1, 1 };

    private readonly Border[][] _cards;
    private readonly Border[][] _rings;

    private int _column;
    private int _row;

    /// <summary>Requests closing this screen and returning to Settings (B).</summary>
    public event Action? ExitRequested;

    public SystemLanguageView()
    {
        InitializeComponent();

        _cards = new[]
        {
            new[] { LgCard0, LgCard1, LgCard2 },
            new[] { LgCard3 },
            new[] { LgCard4 },
        };
        _rings = new[]
        {
            new[] { LgRing0, LgRing1, LgRing2 },
            new[] { LgRing3 },
            new[] { LgRing4 },
        };

        UpdateSelection();
    }

    public void Move(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.Up when _row > 0:
                _row--;
                break;
            case GamepadButton.Down when _row < ColumnHeights[_column] - 1:
                _row++;
                break;
            case GamepadButton.Left when _column > 0:
                _column--;
                break;
            case GamepadButton.Right when _column < _cards.Length - 1:
                _column++;
                break;
            default:
                return; // includes A: nothing here does anything yet
        }

        if (_row > ColumnHeights[_column] - 1)
        {
            _row = ColumnHeights[_column] - 1;
        }

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        for (var c = 0; c < _cards.Length; c++)
        {
            for (var r = 0; r < _cards[c].Length; r++)
            {
                var selected = c == _column && r == _row;
                _cards[c][r].Classes.Set("selected", selected);
                _rings[c][r].Classes.Set("selected", selected);
            }
        }
    }
}
