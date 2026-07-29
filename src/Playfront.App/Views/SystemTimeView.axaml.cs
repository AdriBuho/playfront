using System;
using System.Globalization;
using Playfront.App.Input;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Playfront.App.Views;

/// <summary>
/// The "System Time" screen (Settings -> System -> Time). Mounted on demand and released on B.
///
/// The three cards take the ring but do nothing. Time, date and time zone come from the machine, and
/// the clock ticks while the screen is open.
/// </summary>
public partial class SystemTimeView : UserControl
{
    private readonly Border[] _cards;
    private readonly Border[] _rings;
    private readonly DispatcherTimer _timer;
    private int _index;

    /// <summary>Requests closing this screen and returning to Settings (B).</summary>
    public event Action? ExitRequested;

    public SystemTimeView()
    {
        InitializeComponent();

        _cards = new[] { TmCard0, TmCard1, TmCard2 };
        _rings = new[] { TmRing0, TmRing1, TmRing2 };

        TmZone.Text = TimeZoneName();

        // One tick a second, and only while the screen is mounted: it is stopped on detach so it does
        // not keep waking the app up behind whatever the user goes back to.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshClock();
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();

        RefreshClock();
        UpdateSelection();
    }

    public void Move(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.Up when _index > 0:
                _index--;
                break;
            case GamepadButton.Down when _index < _cards.Length - 1:
                _index++;
                break;
            default:
                return; // includes A: nothing here does anything yet
        }

        UpdateSelection();
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

    // The base offset plus the city, in English: "(UTC+01:00) Madrid".
    //
    // TimeZoneInfo.DisplayName cannot be used here - Windows hands it over already translated, so on a
    // Spanish install this screen said "(UTC+01:00) Bruselas" in the middle of an English interface,
    // and the registry holds the translated string too. The IANA id ("Europe/Madrid") is invariant, so
    // its city part is taken from there instead. The offset is the BASE one, not today's: the reference
    // shows +01:00 in July, when the clock is actually on +02:00 for summer time.
    private static string TimeZoneName()
    {
        try
        {
            var zone = TimeZoneInfo.Local;
            var offset = zone.BaseUtcOffset;
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            var stamp = $"(UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00})";

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && iana is not null)
            {
                var slash = iana.LastIndexOf('/');
                var city = (slash >= 0 ? iana[(slash + 1)..] : iana).Replace('_', ' ');
                return $"{stamp} {city}";
            }

            return $"{stamp} {zone.Id}";
        }
        catch
        {
            return "Unavailable";
        }
    }

    // Invariant culture on purpose: the interface is English, so the date has to read "Wednesday,
    // July 29, 2026" whatever language Windows is in. The time is 24-hour, matching the card below it.
    private void RefreshClock()
    {
        var now = DateTime.Now;
        TmClock.Text = now.ToString("H:mm", CultureInfo.InvariantCulture);
        TmDate.Text = now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);
    }
}
