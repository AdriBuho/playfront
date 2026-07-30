using System;
using System.Globalization;
using Playfront.App.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Playfront.App.Views;

/// <summary>
/// The "Preferences Idle options" screen (Settings -> Preferences -> Idle options). Mounted on demand
/// and released on B.
///
/// VISUAL ONLY: the four cards take the ring and move with the d-pad, but A does nothing and the values
/// shown are the reference's, not this machine's.
/// </summary>
public partial class PreferencesIdleView : UserControl
{
    // Column 0 carries two controls, columns 1 and 2 one each. Moving sideways into a one-card column
    // clamps to its only row, the same rule Personalization and Language use.
    private static readonly int[] ColumnHeights = { 2, 1, 1 };

    // Dropdown geometry, measured from the reference. The panel, the ring and the thumb are anchored
    // in the XAML; everything here moves them from there with a transform, which is what can be
    // animated - Canvas.Top cannot.
    private const int RowHeight = 68;
    private const int VisibleRows = 7;
    private const double PanelWidth = 438;
    private const double TrackHeight = 472;

    /// <summary>
    /// The durations both dropdowns are built from. NOT a regular progression: five-minute steps to
    /// half an hour, then 45 minutes, then whole hours. Read off the reference, and the scrollbar
    /// arithmetic agrees the first list has 13 entries - its thumb covers 53.4% of the track, and
    /// 7 visible rows / 0.534 = 13.1.
    ///
    /// Kept as bare durations, not as finished labels, because the SAME duration appears in both
    /// lists with different wording: "6 hours of inactivity" above, "6 hours of video or music"
    /// below. One list of durations means the two can never drift apart.
    /// </summary>
    private static readonly string[] IdleDurations =
    {
        "10 minutes", "15 minutes", "20 minutes", "25 minutes", "30 minutes", "45 minutes",
        "1 hour", "2 hours", "3 hours", "4 hours", "5 hours", "6 hours",
    };

    private const string NeverTurnOff = "Don't turn off automatically";
    private const string NeverForMedia = "Don't turn off for media";

    /// <summary>The upper dropdown: every duration, then "never".</summary>
    private static string[] BuildTurnOffOptions()
    {
        var options = new string[IdleDurations.Length + 1];
        for (var i = 0; i < IdleDurations.Length; i++)
        {
            options[i] = $"{IdleDurations[i]} of inactivity";
        }

        options[^1] = NeverTurnOff;
        return options;
    }

    /// <summary>
    /// The lower dropdown, which DEPENDS ON THE UPPER ONE.
    ///
    /// The media timeout is an EXTRA granted while something is playing, so it only makes sense for it
    /// to outlast the plain inactivity timeout: the list offers the durations LONGER than the one set
    /// above, never a shorter one. Set the minimum above and every other duration is on offer; set
    /// something long and the list is short.
    ///
    /// With "Don't turn off automatically" above there is no timeout to extend, so the only row is
    /// "never".
    ///
    /// The last-resort branch: with the LONGEST duration set above there is nothing longer to offer,
    /// and the reference does not show an empty list - it shows that same duration. So it is used as
    /// the floor rather than leaving the user a dropdown with one dead row.
    /// </summary>
    private static string[] BuildMediaOptions(string turnOffValue)
    {
        var index = DurationIndexOf(turnOffValue);
        if (index < 0)
        {
            return new[] { NeverForMedia };
        }

        var first = index + 1;
        if (first >= IdleDurations.Length)
        {
            first = index; // nothing longer exists: offer the same one
        }

        var options = new string[IdleDurations.Length - first + 1];
        for (var i = first; i < IdleDurations.Length; i++)
        {
            options[i - first] = $"{IdleDurations[i]} of video or music";
        }

        options[^1] = NeverForMedia;
        return options;
    }

    /// <summary>Position in IdleDurations of a "... of inactivity" label, or -1 for "never".</summary>
    private static int DurationIndexOf(string turnOffValue)
    {
        for (var i = 0; i < IdleDurations.Length; i++)
        {
            if (turnOffValue.StartsWith(IdleDurations[i] + " of", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static readonly string[] TurnOffOptions = BuildTurnOffOptions();

    private readonly Border[][] _cards;
    private readonly Border[][] _rings;

    private int _column;
    private int _row;

    private Border[]? _offRows;
    private bool _offOpen;

    private string[] _medOptions = Array.Empty<string>();
    private bool _medOpen;
    private int _medIndex;
    private int _medTop;

    /// <summary>Rows this list shows at once: seven, or fewer when it has fewer options.</summary>
    private int _medVisible;

    /// <summary>
    /// Centre of the card the media dropdown replaces, which is where its selected row lands.
    ///
    /// The UPPER dropdown does not follow this rule and is pinned in the XAML instead: with "15
    /// minutes" selected the reference puts it at y=312, which this rule does not produce. One
    /// capture of each is not enough to explain the difference, so each is placed as measured.
    /// </summary>
    private const double MediaCardCentre = 868;

    /// <summary>Option under the ring while the dropdown is open, as an index into TurnOffOptions.</summary>
    private int _offIndex;

    /// <summary>First option showing in the seven-row window.</summary>
    private int _offTop;

    /// <summary>Requests closing this screen and returning to Settings (B).</summary>
    public event Action? ExitRequested;

    /// <summary>Asks for the controller's accelerating auto-repeat while a long list is open.</summary>
    public event Action<bool>? RepeatRequested;

    public PreferencesIdleView()
    {
        InitializeComponent();

        _cards = new[]
        {
            new[] { IdCard0, IdCard1 },
            new[] { IdCard2 },
            new[] { IdCard3 },
        };
        _rings = new[]
        {
            new[] { IdRing0, IdRing1 },
            new[] { IdRing2 },
            new[] { IdRing3 },
        };

        UpdateSelection();
    }

    public void Move(GamepadButton button)
    {
        // While a dropdown is up it owns the gamepad: nothing behind it may move.
        if (_offOpen)
        {
            MoveTurnOffDropdown(button);
            return;
        }

        if (_medOpen)
        {
            MoveMediaDropdown(button);
            return;
        }

        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;

            // Only the two left-hand cards open anything so far.
            case GamepadButton.A when _column == 0 && _row == 0:
                OpenTurnOffDropdown();
                return;

            case GamepadButton.A when _column == 0 && _row == 1:
                OpenMediaDropdown();
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

    // ---- "Turn off after" dropdown ----------------------------------------------------------

    /// <summary>
    /// Creates one row per option. Built on first use: opening this list is not the common reason for
    /// coming to this screen. One control per option rather than a handful recycled, because recycled
    /// rows can only swap labels - they cannot slide.
    /// </summary>
    private void BuildTurnOffRows()
    {
        var rows = new Border[TurnOffOptions.Length];

        for (var i = 0; i < TurnOffOptions.Length; i++)
        {
            var text = new TextBlock { Text = TurnOffOptions[i] };
            text.Classes.Add("offOption");

            var row = new Border { Width = PanelWidth, Height = RowHeight, Child = text };

            // -2 on both axes cancels the panel border's inset, so rows line up with its OUTER box.
            Canvas.SetLeft(row, -2);
            Canvas.SetTop(row, i * RowHeight - 2);

            rows[i] = row;
            OffList.Children.Add(row);
        }

        _offRows = rows;
    }

    private void OpenTurnOffDropdown()
    {
        if (_offRows is null)
        {
            BuildTurnOffRows();
        }

        _offIndex = Array.IndexOf(TurnOffOptions, IdValue0.Text);
        if (_offIndex < 0)
        {
            _offIndex = TurnOffOptions.Length - 1; // the reference's default is the last one
        }

        _offOpen = true;
        RepeatRequested?.Invoke(true);

        // The card is NOT hidden, unlike the time zone one: the reference keeps it drawn and merely
        // unselected, so the strip of it below the panel reads as an unselected card.
        IdCard0.Classes.Set("selected", false);
        IdRing0.IsVisible = false;

        UpdateTurnOffWindow(animate: false);

        // Open from the card's slot: one row tall where the card was, then grow. The list transition
        // is suspended while the opening position is applied, or the list would glide from wherever it
        // was left last time on top of the unfold.
        var offset = (_offIndex - _offTop) * (double)RowHeight;
        var rest = ListRestOffset();
        var listTransitions = OffList.Transitions;
        OffList.Transitions = null;

        OffPanel.Height = RowHeight;
        OffPanel.RenderTransform = Translate(offset);
        OffList.RenderTransform = Translate(rest - offset);
        SetInstant(OffHighlight, 0);
        OffPanel.IsVisible = true;
        OffRing.IsVisible = true;
        OffThumb.IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            OffList.Transitions = listTransitions;
            OffPanel.Height = RowHeight * VisibleRows;
            OffPanel.RenderTransform = Translate(0);
            OffList.RenderTransform = Translate(rest);
            OffHighlight.RenderTransform = Translate(offset);
        });
    }

    private void CloseTurnOffDropdown()
    {
        _offOpen = false;
        OffPanel.IsVisible = false;
        OffRing.IsVisible = false;
        OffThumb.IsVisible = false;
        RepeatRequested?.Invoke(false);
        UpdateSelection(); // gives the card its ring back
    }

    private void MoveTurnOffDropdown(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.A:
                // Only the label changes for now: what this should do to Windows is not decided yet.
                IdValue0.Text = TurnOffOptions[_offIndex];
                SyncMediaValue();
                CloseTurnOffDropdown();
                return;
            case GamepadButton.B:
                CloseTurnOffDropdown();
                return;
            case GamepadButton.Up when _offIndex > 0:
                _offIndex--;
                break;
            case GamepadButton.Down when _offIndex < TurnOffOptions.Length - 1:
                _offIndex++;
                break;
            default:
                return;
        }

        UpdateTurnOffWindow();
    }

    /// <summary>
    /// Slides the list so the selected option lands on the middle row, and moves the ring and the
    /// thumb with it. In the body of the list the ring does not move at all - the list glides under
    /// it. Only near the two ends, where there is nothing left to scroll, does the ring travel.
    /// </summary>
    private void UpdateTurnOffWindow(bool animate = true)
    {
        var maxTop = Math.Max(0, TurnOffOptions.Length - VisibleRows);
        _offTop = Math.Clamp(_offIndex - VisibleRows / 2, 0, maxTop);

        var selectionTop = (_offIndex - _offTop) * (double)RowHeight;

        if (!animate)
        {
            SetInstant(OffList, ListRestOffset());
            SetInstant(OffRing, selectionTop);
            SetInstant(OffHighlight, selectionTop);
        }
        else
        {
            OffList.RenderTransform = Translate(ListRestOffset());
            OffRing.RenderTransform = Translate(selectionTop);
            OffHighlight.RenderTransform = Translate(selectionTop);
        }

        var thumb = Math.Max(24, TrackHeight * VisibleRows / TurnOffOptions.Length);
        var travel = maxTop == 0 ? 0 : (double)_offTop / maxTop;
        OffThumb.Height = thumb;
        if (!animate)
        {
            SetInstant(OffThumb, travel * (TrackHeight - thumb));
        }
        else
        {
            OffThumb.RenderTransform = Translate(travel * (TrackHeight - thumb));
        }
    }

    /// <summary>Where the list sits when the window starts at <see cref="_offTop"/>.</summary>
    private double ListRestOffset() => -_offTop * (double)RowHeight;

    // ---- "for media" dropdown ---------------------------------------------------------------

    /// <summary>
    /// Keeps the media card consistent with the one above after that one changes. Raising the
    /// inactivity timeout can leave the media value below it - "1 hour of video or music" with "2
    /// hours of inactivity" above - and that combination is no longer on offer, so it cannot stay on
    /// screen.
    ///
    /// Falls back to "never", which is the one row that is always there. Not measured: the reference
    /// was never seen mid-change, so this is the safe reading rather than an observed one.
    /// </summary>
    private void SyncMediaValue()
    {
        var options = BuildMediaOptions(IdValue0.Text ?? string.Empty);
        if (!Array.Exists(options, o => o == IdValue1.Text))
        {
            IdValue1.Text = NeverForMedia;
        }
    }

    private void OpenMediaDropdown()
    {
        _medOptions = BuildMediaOptions(IdValue0.Text ?? string.Empty);

        _medIndex = Array.IndexOf(_medOptions, IdValue1.Text);
        if (_medIndex < 0)
        {
            _medIndex = _medOptions.Length - 1;
        }

        // Rebuilt on every open, unlike the upper list: its contents change with the card above, so
        // caching it would only be a way to show a stale option.
        MedList.Children.Clear();
        for (var i = 0; i < _medOptions.Length; i++)
        {
            var text = new TextBlock { Text = _medOptions[i] };
            text.Classes.Add("offOption");

            var row = new Border { Width = PanelWidth, Height = RowHeight, Child = text };
            Canvas.SetLeft(row, -2);
            Canvas.SetTop(row, i * RowHeight - 2);
            MedList.Children.Add(row);
        }

        // Never taller than seven rows, but often shorter: the list can be anything from one row to
        // twelve depending on the card above.
        _medVisible = Math.Min(VisibleRows, _medOptions.Length);
        _medTop = Math.Clamp(_medIndex - _medVisible / 2, 0, _medOptions.Length - _medVisible);

        _medOpen = true;
        RepeatRequested?.Invoke(_medOptions.Length > _medVisible);
        IdCard1.Classes.Set("selected", false);
        IdRing1.IsVisible = false;

        // The selected row lands on the card - the rule this dropdown follows - and the panel then
        // stays put, with the list scrolling underneath it.
        var visiblePos = (_medIndex - _medTop) * (double)RowHeight;
        var panelTop = MediaCardCentre - RowHeight / 2.0 - visiblePos;
        Canvas.SetTop(MedPanel, panelTop);
        Canvas.SetTop(MedRing, panelTop);
        Canvas.SetTop(MedThumb, panelTop + 2);

        UpdateMediaWindow(animate: false);

        // One row tall in the card's slot, with the list shifted so the row on show is the selected
        // one; the panel's own translate cancels the difference. Without the list shift the unfold
        // would start on row 0 whatever was selected.
        var rest = -_medTop * (double)RowHeight;
        var listTransitions = MedList.Transitions;
        MedList.Transitions = null;

        MedPanel.Height = RowHeight;
        MedPanel.RenderTransform = Translate(visiblePos);
        MedList.RenderTransform = Translate(rest - visiblePos);
        SetInstant(MedHighlight, 0);
        MedPanel.IsVisible = true;
        MedRing.IsVisible = true;
        MedThumb.IsVisible = _medOptions.Length > _medVisible;

        Dispatcher.UIThread.Post(() =>
        {
            MedList.Transitions = listTransitions;
            MedPanel.Height = RowHeight * _medVisible;
            MedPanel.RenderTransform = Translate(0);
            MedList.RenderTransform = Translate(rest);
            MedHighlight.RenderTransform = Translate(visiblePos);
        });
    }

    private void CloseMediaDropdown()
    {
        _medOpen = false;
        MedPanel.IsVisible = false;
        MedRing.IsVisible = false;
        MedThumb.IsVisible = false;
        RepeatRequested?.Invoke(false);
        UpdateSelection();
    }

    private void MoveMediaDropdown(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.A:
                IdValue1.Text = _medOptions[_medIndex];
                CloseMediaDropdown();
                return;
            case GamepadButton.B:
                CloseMediaDropdown();
                return;
            case GamepadButton.Up when _medIndex > 0:
                _medIndex--;
                break;
            case GamepadButton.Down when _medIndex < _medOptions.Length - 1:
                _medIndex++;
                break;
            default:
                return;
        }

        UpdateMediaWindow();
    }

    /// <summary>Same rule as the upper list: the list slides, the selection stays on the middle row.</summary>
    private void UpdateMediaWindow(bool animate = true)
    {
        var maxTop = Math.Max(0, _medOptions.Length - _medVisible);
        _medTop = Math.Clamp(_medIndex - _medVisible / 2, 0, maxTop);

        var selectionTop = (_medIndex - _medTop) * (double)RowHeight;
        var rest = -_medTop * (double)RowHeight;

        if (!animate)
        {
            SetInstant(MedList, rest);
            SetInstant(MedRing, selectionTop);
            SetInstant(MedHighlight, selectionTop);
        }
        else
        {
            MedList.RenderTransform = Translate(rest);
            MedRing.RenderTransform = Translate(selectionTop);
            MedHighlight.RenderTransform = Translate(selectionTop);
        }

        if (maxTop == 0)
        {
            return; // everything fits: no thumb to place
        }

        var track = _medVisible * (double)RowHeight - 4;
        var thumb = Math.Max(24, track * _medVisible / _medOptions.Length);
        MedThumb.Height = thumb;
        var travel = (double)_medTop / maxTop * (track - thumb);
        if (!animate)
        {
            SetInstant(MedThumb, travel);
        }
        else
        {
            MedThumb.RenderTransform = Translate(travel);
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
