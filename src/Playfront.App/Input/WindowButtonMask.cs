using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Playfront.App.Input;

/// <summary>
/// Covers the close / minimise / maximise buttons of an outside program while the controller is
/// driving it, so a stray click cannot shut it down or drop the user onto the desktop.
///
/// WHY A COVER AND NOT A REMOVAL. Three routes were tried and measured; this one is what survived:
///
///   - Stripping WS_CAPTION and friends removes the WINDOWS frame and does nothing here: a
///     Chromium-based program (Spotify, Discord, VS Code...) paints its own title bar inside the
///     client area, so those buttons are just pixels the program drew.
///   - F11 does nothing either. Spotify has no fullscreen mode of its own.
///   - Pushing that whole row off the top of the screen DOES work - move the window up by the height
///     of the bar and make it taller by the same amount, so the bottom edge still lands on the screen.
///     It even beats the program's own size limit, which refuses anything over screen + 20 px
///     (measured: 1144, 1200 and 1400 all came back 1100) because SWP_NOSENDCHANGING stops Windows
///     asking it. It was built, and then thrown away: that row is not just buttons. Logged in it also
///     holds the SEARCH BOX, Home, back and forward, notifications and the account button. Losing
///     search to hide three buttons is a bad trade, and the logged-out window hides this - there the
///     row really is empty, which is how the measurement went wrong the first time.
///
/// So the buttons stay where they are and a small opaque window sits on top of them. It is not
/// click-through on purpose: swallowing the click IS the point.
/// </summary>
public sealed class WindowButtonMask : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    // Measured on Spotify at 100% scaling. Hovering each button lights up its hit area, which is how
    // these came out rather than by eye: 45 wide and the FULL height of the bar - the middle button
    // ran x 1830..1874, y 0..63. Three of them, flush to the right edge, so 135 x 64 anchored to the
    // top right corner. Standard Chromium caption buttons, not something Spotify invented, so the
    // numbers travel to other programs built the same way.
    private const double StripWidth = 135;
    private const double StripHeight = 64;

    // PURE BLACK, and the "pure" matters. Measured across the whole bar with an account signed in, on
    // two different pages: 0,0,0 everywhere, including behind the buttons and around the account
    // picture. The page below starts at exactly y 64 and is NOT black (86,21,55 on the page it was
    // measured on), which is why the height must not creep past 64.
    //
    // An earlier reading of 25,29,33 came from a SIGNED-OUT window, where the bar is a different,
    // lighter colour. Anything measured on Spotify signed out says nothing about the real interface.
    private static readonly Color StripColour = Color.FromRgb(0x00, 0x00, 0x00);

    private readonly PixelRect _monitor;

    /// <param name="monitor">Full bounds of the monitor the covered program is on, in real pixels.</param>
    public WindowButtonMask(PixelRect monitor)
    {
        _monitor = monitor;

        WindowDecorations = WindowDecorations.None;
        Background = new SolidColorBrush(StripColour);
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Width = StripWidth;
        Height = StripHeight;

        Opened += (_, _) =>
        {
            ApplyStyles();
            Place();
        };
    }

    // Anchored to the top-right corner of the monitor rather than placed at a fixed coordinate, which
    // is what makes the resolution irrelevant: the buttons are always in that corner, whatever the
    // screen measures. Checked by giving Spotify a 1280x720 window - the three buttons came out the
    // same 135 x 64 as at 1920x1080, 23.5 px from the right edge to the centre of the last one, and
    // the bar still 64 tall.
    //
    // Width/Height are in device-independent units and Position is in real pixels, so the corner has
    // to be worked out with the scaling factor. Not a detail that can be skipped: on a 150% display
    // the strip is 202 px wide, and Chromium scales its buttons the same way.
    private void Place()
    {
        void Apply()
        {
            var scale = RenderScaling > 0 ? RenderScaling : 1.0;
            Position = new PixelPoint(
                (int)Math.Round(_monitor.Right - StripWidth * scale),
                _monitor.Y);
        }

        Apply();

        // Once more after the layout settles. The first pass reads the scaling of wherever the window
        // happened to open, which is not necessarily the monitor it is being sent to - and on a setup
        // mixing a 100% screen with a 150% one those are different numbers.
        Dispatcher.UIThread.Post(Apply, DispatcherPriority.Loaded);
    }

    private void ApplyStyles()
    {
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            // No WS_EX_TRANSPARENT here, unlike the hint badge: this one has to STOP clicks, not let
            // them through. NOACTIVATE keeps it from stealing focus - taking focus off the program
            // would pause what is playing - and TOOLWINDOW keeps it out of Alt+Tab and the taskbar.
            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        }
        catch (Exception e)
        {
            // Worst case it is a small dark rectangle that also takes focus when clicked. Ugly, not
            // dangerous, and the way out of pointer mode still works.
            CrashLog.Log("Could not set the styles on the button mask", e);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
