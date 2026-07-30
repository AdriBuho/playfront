using System;
using System.Globalization;
using Playfront.App.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Playfront.App.Views;

/// <summary>
/// The "Preferences Break reminder" screen (Settings -> Preferences -> Break reminder). Mounted on
/// demand and released on B.
///
/// The single card opens its dropdown, and choosing an option changes the label. Nothing reaches
/// Windows yet: what a break reminder should actually do here is not decided.
/// </summary>
public partial class PreferencesBreakView : UserControl
{
    // Dropdown geometry, measured from the reference. These rows are 56 tall, NOT the 68 the time zone
    // and "Turn off after" lists use - the console has both heights and this is the short one.
    private const int RowHeight = 56;
    private const double PanelWidth = 438;

    /// <summary>
    /// The intervals, in the console's order. Note the wording is not uniform - "Every hour", not
    /// "Every 1 hour" - so these are written out rather than generated from a number.
    /// </summary>
    private static readonly string[] Intervals =
    {
        "Never",
        "Every 30 minutes",
        "Every hour",
        "Every 1 hour 30 minutes",
        "Every 2 hours",
    };

    private Border[]? _rows;
    private bool _open;
    private int _index;

    /// <summary>Requests closing this screen and returning to Settings (B).</summary>
    public event Action? ExitRequested;

    public PreferencesBreakView()
    {
        InitializeComponent();

        // One card, so it is selected from the moment the screen opens and never moves.
        BrkCard0.Classes.Set("selected", true);
        BrkRing0.Classes.Set("selected", true);
    }

    public void Move(GamepadButton button)
    {
        if (_open)
        {
            MoveDropdown(button);
            return;
        }

        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.A:
                OpenDropdown();
                return;
        }
    }

    private void OpenDropdown()
    {
        if (_rows is null)
        {
            var rows = new Border[Intervals.Length];
            for (var i = 0; i < Intervals.Length; i++)
            {
                var text = new TextBlock { Text = Intervals[i] };
                text.Classes.Add("brkOption");

                var row = new Border { Width = PanelWidth, Height = RowHeight, Child = text };

                // -2 on both axes cancels the panel border's inset, so rows line up with its OUTER box.
                Canvas.SetLeft(row, -2);
                Canvas.SetTop(row, i * RowHeight - 2);

                rows[i] = row;
                BrkList.Children.Add(row);
            }

            _rows = rows;
        }

        _index = Array.IndexOf(Intervals, BrkValue0.Text);
        if (_index < 0)
        {
            _index = 0;
        }

        _open = true;
        BrkCard0.Classes.Set("selected", false);
        BrkRing0.IsVisible = false;

        SelectRow(animate: false);

        // Open from the card's slot: one row tall, with the list shifted so the row on show is the
        // selected one, then grow. The list transition is suspended for the opening position or it
        // would glide from wherever it was left last time on top of the unfold.
        var offset = _index * (double)RowHeight;
        var listTransitions = BrkList.Transitions;
        BrkList.Transitions = null;

        BrkPanel.Height = RowHeight;
        BrkPanel.RenderTransform = Translate(offset);
        BrkList.RenderTransform = Translate(-offset);
        SetInstant(BrkHighlight, 0);
        BrkPanel.IsVisible = true;
        BrkRing.IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            BrkList.Transitions = listTransitions;
            BrkPanel.Height = RowHeight * Intervals.Length;
            BrkPanel.RenderTransform = Translate(0);
            BrkList.RenderTransform = Translate(0);
            BrkHighlight.RenderTransform = Translate(offset);
        });
    }

    private void CloseDropdown()
    {
        _open = false;
        BrkPanel.IsVisible = false;
        BrkRing.IsVisible = false;
        BrkCard0.Classes.Set("selected", true);
        BrkRing0.IsVisible = true;
    }

    private void MoveDropdown(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.A:
                // Only the label changes for now: what this should do on a PC is not decided yet.
                BrkValue0.Text = Intervals[_index];
                CloseDropdown();
                return;
            case GamepadButton.B:
                CloseDropdown();
                return;
            case GamepadButton.Up when _index > 0:
                _index--;
                break;
            case GamepadButton.Down when _index < Intervals.Length - 1:
                _index++;
                break;
            default:
                return;
        }

        SelectRow();
    }

    /// <summary>
    /// Moves the ring and the fill onto the current row. The panel itself never moves: all five
    /// options are on screen at once, so there is nothing to scroll.
    /// </summary>
    private void SelectRow(bool animate = true)
    {
        var y = _index * (double)RowHeight;
        if (animate)
        {
            BrkRing.RenderTransform = Translate(y);
            BrkHighlight.RenderTransform = Translate(y);
        }
        else
        {
            SetInstant(BrkRing, y);
            SetInstant(BrkHighlight, y);
        }
    }

    private static ITransform Translate(double y) =>
        TransformOperations.Parse($"translateY({y.ToString(CultureInfo.InvariantCulture)}px)");

    /// <summary>Moves a control with no animation, by taking its transitions away for the assignment.</summary>
    private static void SetInstant(Control control, double y)
    {
        var saved = control.Transitions;
        control.Transitions = null;
        control.RenderTransform = Translate(y);
        control.Transitions = saved;
    }
}
