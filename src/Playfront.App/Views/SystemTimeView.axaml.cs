using System;
using System.Globalization;
using System.Linq;
using Playfront.App.Input;
using Playfront.App.System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Playfront.App.Views;

/// <summary>
/// The "System Time" screen (Settings -> System -> Time). Mounted on demand and released on B.
///
/// The three controls act on Windows itself through SystemTimeSettings: the time zone, the automatic
/// daylight-saving adjustment and the 12/24-hour clock. Nothing is cached in the app - the state is
/// read back from Windows after every change, so what is on screen is what the system actually holds.
/// </summary>
public partial class SystemTimeView : UserControl
{
    // Dropdown geometry, all measured from the reference. The panel, the ring and the scrollbar are
    // anchored in the XAML (panel and ring at y=75, thumb at y=77); everything here moves them from
    // there with a transform, which is what can be animated - Canvas.Top cannot.
    private const int RowHeight = 68;
    private const int VisibleRows = 7;
    private const double PanelWidth = 778;
    private const double TrackHeight = 471;

    private readonly Border[] _cards;
    private readonly Border[] _rings;
    private readonly DispatcherTimer _timer;
    private int _index;

    /// <summary>One row control per zone, built once by BuildTimeZoneRows.</summary>
    private Border[]? _tzRows;

    private bool _tzOpen;

    /// <summary>Zone under the ring while the dropdown is open, as an index into WindowsTimeZones.All.</summary>
    private int _tzIndex;

    /// <summary>First zone showing in the seven-row window.</summary>
    private int _tzTop;

    /// <summary>Zone in force when the dropdown opened, so B can put it back.</summary>
    private string _tzOriginal = string.Empty;

    /// <summary>Requests closing this screen and returning to Settings (B).</summary>
    public event Action? ExitRequested;

    /// <summary>Raised when the 12/24-hour choice changed, so other clocks in the app can follow.</summary>
    public event Action? ClockFormatChanged;

    /// <summary>
    /// Asks for the controller's accelerating auto-repeat to be turned on or off. On for the time zone
    /// list: it is 141 entries long, and without it reaching the far end takes 140 separate presses.
    /// The keyboard needs nothing here - Windows repeats a held key on its own.
    /// </summary>
    public event Action<bool>? RepeatRequested;

    public SystemTimeView()
    {
        InitializeComponent();

        _cards = new[] { TmCard0, TmCard1, TmCard2 };
        _rings = new[] { TmRing0, TmRing1, TmRing2 };

        // One tick a second, and only while the screen is mounted: it is stopped on detach so it does
        // not keep waking the app up behind whatever the user goes back to.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshClock();
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();

        RefreshFromSystem();
        UpdateSelection();
    }

    public void Move(GamepadButton button)
    {
        // While the dropdown is up it owns the gamepad: nothing behind it may move.
        if (_tzOpen)
        {
            MoveTimeZoneDropdown(button);
            return;
        }

        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.A:
                Activate();
                return;
            case GamepadButton.Up when _index > 0:
                _index--;
                break;
            case GamepadButton.Down when _index < _cards.Length - 1:
                _index++;
                break;
            default:
                return;
        }

        UpdateSelection();
    }

    private void Activate()
    {
        switch (_index)
        {
            case 0:
                OpenTimeZoneDropdown();
                return;
            case 1:
                Report(SystemTimeSettings.SetAutoDaylightSaving(!SystemTimeSettings.AutoDaylightSaving()));
                break;
            case 2:
                var result = SystemTimeSettings.Set24HourClock(!SystemTimeSettings.Use24HourClock());
                Report(result);
                if (result.Ok)
                {
                    ClockFormatChanged?.Invoke();
                }

                break;
        }

        RefreshFromSystem();
    }

    // ---- time zone dropdown -----------------------------------------------------------------

    /// <summary>
    /// Creates one row per zone. Built on first use rather than in the constructor: opening this list
    /// is not the common reason for coming to this screen.
    /// </summary>
    private void BuildTimeZoneRows()
    {
        var zones = WindowsTimeZones.All;
        var rows = new Border[zones.Count];

        for (var i = 0; i < zones.Count; i++)
        {
            var text = new TextBlock { Text = zones[i].Label };
            text.Classes.Add("tzOption");

            var row = new Border
            {
                Width = PanelWidth,
                Height = RowHeight,
                Child = text,
            };

            // -2 on both axes cancels the panel border's inset, so rows line up with its OUTER box.
            Canvas.SetLeft(row, -2);
            Canvas.SetTop(row, i * RowHeight - 2);

            rows[i] = row;
            TzList.Children.Add(row);
        }

        _tzRows = rows;
    }

    private void OpenTimeZoneDropdown()
    {
        var zones = WindowsTimeZones.All;
        if (zones.Count == 0)
        {
            Report(TimeChangeResult.Failed("Windows reported no time zones."));
            return;
        }

        if (_tzRows is null)
        {
            BuildTimeZoneRows();
        }

        _tzOriginal = SystemTimeSettings.CurrentTimeZoneId();
        _tzIndex = Math.Max(0, zones.ToList().FindIndex(
            z => string.Equals(z.Id, _tzOriginal, StringComparison.OrdinalIgnoreCase)));
        _tzOpen = true;

        // The panel replaces the card, so the card and its ring go away for as long as it is up.
        TmCard0.IsVisible = false;
        TmRing0.IsVisible = false;
        RepeatRequested?.Invoke(true);

        UpdateTimeZoneWindow(animate: false);

        // Open from the card's slot: one row tall where the card was, with the list shifted so the row
        // on show is the selected one, then grow.
        //
        // The list transition is suspended while the OPENING position is applied. Leaving it on would
        // make the list glide from wherever it was left last time, on top of the unfold - two
        // animations fighting. It is restored just before the end state so the unfold itself animates.
        var offset = (_tzIndex - _tzTop) * (double)RowHeight;
        var rest = ListRestOffset();
        var listTransitions = TzList.Transitions;
        TzList.Transitions = null;

        TzPanel.Height = RowHeight;
        TzPanel.RenderTransform = Translate(offset);
        TzList.RenderTransform = Translate(rest - offset);
        // While the panel is one row tall the selected row sits at its very top, so the fill starts
        // there and travels down to its slot as the panel unfolds around it.
        SetInstant(TzHighlight, 0);
        TzPanel.IsVisible = true;
        TzRing.IsVisible = true;
        TzThumb.IsVisible = true;

        // Setting the end state in this same pass would skip the motion entirely.
        Dispatcher.UIThread.Post(() =>
        {
            TzList.Transitions = listTransitions;
            TzPanel.Height = RowHeight * VisibleRows;
            TzPanel.RenderTransform = Translate(0);
            TzList.RenderTransform = Translate(rest);
            TzHighlight.RenderTransform = Translate(offset);
        });
    }

    private static ITransform Translate(double y) =>
        TransformOperations.Parse($"translateY({y.ToString(CultureInfo.InvariantCulture)}px)");

    /// <summary>Where the list sits when the window starts at <see cref="_tzTop"/>.</summary>
    private double ListRestOffset() => -_tzTop * (double)RowHeight;

    private void CloseTimeZoneDropdown()
    {
        _tzOpen = false;
        TzPanel.IsVisible = false;
        TzRing.IsVisible = false;
        TzThumb.IsVisible = false;
        TmCard0.IsVisible = true;
        TmRing0.IsVisible = true;
        RepeatRequested?.Invoke(false);
    }

    private void MoveTimeZoneDropdown(GamepadButton button)
    {
        var zones = WindowsTimeZones.All;

        switch (button)
        {
            case GamepadButton.A:
                Report(SystemTimeSettings.SetTimeZone(zones[_tzIndex].Id));
                CloseTimeZoneDropdown();
                RefreshFromSystem();
                return;
            case GamepadButton.B:
                // Nothing was written while moving through the list, so backing out only needs to
                // drop the panel.
                CloseTimeZoneDropdown();
                RefreshFromSystem();
                return;
            case GamepadButton.Up when _tzIndex > 0:
                _tzIndex--;
                break;
            case GamepadButton.Down when _tzIndex < zones.Count - 1:
                _tzIndex++;
                break;
            default:
                return;
        }

        UpdateTimeZoneWindow();
    }

    /// <summary>
    /// Slides the list so the selected zone lands on the middle row, and moves the ring and the thumb
    /// with it.
    ///
    /// Nothing is rebuilt here: the rows exist and simply travel. In the body of the list the ring
    /// does not move at all - the list glides under it, which is what makes it read like a phone
    /// picker. Only near the two ends, where there is nothing left to scroll, does the ring travel
    /// instead.
    /// </summary>
    private void UpdateTimeZoneWindow(bool animate = true)
    {
        var zones = WindowsTimeZones.All;
        var maxTop = Math.Max(0, zones.Count - VisibleRows);
        _tzTop = Math.Clamp(_tzIndex - VisibleRows / 2, 0, maxTop);

        // The ring and the fill share one position - they are the same indicator - and in the body of
        // the list that position never changes: the list slides and the selection stays put.
        var selectionTop = (_tzIndex - _tzTop) * (double)RowHeight;

        if (!animate)
        {
            SetInstant(TzList, ListRestOffset());
            SetInstant(TzRing, selectionTop);
            SetInstant(TzHighlight, selectionTop);
        }
        else
        {
            TzList.RenderTransform = Translate(ListRestOffset());
            TzRing.RenderTransform = Translate(selectionTop);
            TzHighlight.RenderTransform = Translate(selectionTop);
        }

        // Thumb proportional to the window, exactly as the reference: 7 of 141 rows measured 24 px.
        var thumb = Math.Max(24, TrackHeight * VisibleRows / zones.Count);
        var travel = maxTop == 0 ? 0 : (double)_tzTop / maxTop;
        TzThumb.Height = thumb;
        if (!animate)
        {
            SetInstant(TzThumb, travel * (TrackHeight - thumb));
        }
        else
        {
            TzThumb.RenderTransform = Translate(travel * (TrackHeight - thumb));
        }
    }

    /// <summary>Moves a control with no animation, by taking its transitions away for the assignment.</summary>
    private static void SetInstant(Control control, double y)
    {
        var saved = control.Transitions;
        control.Transitions = null;
        control.RenderTransform = Translate(y);
        control.Transitions = saved;
    }

    /// <summary>Pulls every value on screen back out of Windows.</summary>
    private void RefreshFromSystem()
    {
        TmZone.Text = WindowsTimeZones.LabelFor(SystemTimeSettings.CurrentTimeZoneId());
        TmTickDst.IsVisible = SystemTimeSettings.AutoDaylightSaving();
        TmTick24.IsVisible = SystemTimeSettings.Use24HourClock();
        RefreshClock();
    }

    private void Report(TimeChangeResult result)
    {
        TmError.Text = result.Error ?? string.Empty;
        TmError.IsVisible = !result.Ok;
    }

    private void UpdateSelection()
    {
        for (var i = 0; i < _cards.Length; i++)
        {
            var selected = i == _index;
            _cards[i].Classes.Set("selected", selected);
            _rings[i].Classes.Set("selected", selected);
        }
    }

    // Invariant culture on purpose: the interface is English, so the date has to read "Wednesday,
    // July 29, 2026" whatever language Windows is in. The time follows the 24-hour checkbox, which is
    // the account's real Windows setting rather than anything private to the app.
    private void RefreshClock()
    {
        var now = DateTime.Now;
        TmClock.Text = now.ToString(SystemTimeSettings.ClockFormat, CultureInfo.InvariantCulture);
        TmDate.Text = now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);
    }
}
