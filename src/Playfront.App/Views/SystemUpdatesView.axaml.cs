using System;
using Playfront.App.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Playfront.App.Views;

/// <summary>
/// The "System Updates" screen (Settings -> System -> Updates). Mounted on demand and released on B,
/// like every other screen in the shell.
///
/// The first tile is the update state and drives the rest: opening the screen kicks off a check, and
/// depending on the result the tile lights up (there is something to press: download, or restart) or
/// goes dim (nothing to do, and focus skips it). All real logic lives in <see cref="UpdateService"/>;
/// this only paints it.
///
/// The two toggles below are still decorative: they render checked because that is how the reference
/// shows them, and they neither read nor write anything yet.
/// </summary>
public partial class SystemUpdatesView : UserControl
{
    private const int StatusTile = 0;
    private const int LastTile = 3;

    // Colours measured from the reference. The dim tile colour is not invented: it is what
    // "No console update available" actually uses there.
    private static readonly IBrush DisabledBackground = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
    private static readonly IBrush DisabledForeground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
    private static readonly IBrush NormalBackground = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));

    private readonly UpdateService _updates;
    private readonly Border[] _tiles;
    private readonly Border[] _rings;

    // Right-hand description per tile. Only the one for "Latest console update status" is known — it
    // is the only one the reference shows — and text that hasn't been measured is not invented here.
    private static readonly string?[] Descriptions =
    {
        null,
        "See when your console last updated, when it last checked for updates, and what's new in the latest update.",
        null,
        null,
    };

    // Starts on the second tile, where the reference puts the ring. If the check finds an update the
    // first tile lights up but does NOT steal focus: moving the selection under the user's thumb is
    // the last thing a settings screen should do.
    private int _tile = 1;

    /// <summary>Requests closing this screen and returning to Settings (B).</summary>
    public event Action? ExitRequested;

    // Internal because it takes UpdateService, which is internal too. MainWindow constructs it and
    // lives in the same assembly.
    internal SystemUpdatesView(UpdateService updates)
    {
        InitializeComponent();

        _updates = updates;
        _tiles = new[] { UpdTile0, UpdTile1, UpdTile2, UpdTile3 };
        _rings = new[] { UpdRing0, UpdRing1, UpdRing2, UpdRing3 };

        _updates.Changed += OnUpdatesChanged;

        UpdateSelection();

        // Automatic check on entry. Skipped when a download is in flight or staged: re-checking would
        // throw away what has already been downloaded.
        if (_updates.State is UpdateState.Idle or UpdateState.Unsupported
            or UpdateState.UpToDate or UpdateState.Failed)
        {
            _ = _updates.CheckAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Required: this view is destroyed and rebuilt every time the screen opens. Without this,
        // each visit would leave a live subscription pointing at a dead view, and the service — which
        // outlives them all — would accumulate every one of them.
        _updates.Changed -= OnUpdatesChanged;
        base.OnDetachedFromVisualTree(e);
    }

    public void Move(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;

            case GamepadButton.A when _tile == StatusTile:
                ActivateStatusTile();
                return;

            case GamepadButton.Up when _tile > FirstSelectableTile():
                _tile--;
                break;
            case GamepadButton.Down when _tile < LastTile:
                _tile++;
                break;

            default:
                return;
        }

        UpdateSelection();
    }

    // What pressing the status tile does depends on where the process is. States not listed here are
    // not pressable (see StatusTileIsActionable).
    private void ActivateStatusTile()
    {
        switch (_updates.State)
        {
            case UpdateState.Available:
                _ = _updates.DownloadAsync();
                break;
            case UpdateState.ReadyToRestart:
                // Does not return: the process dies and the new version starts.
                _updates.ApplyAndRestart();
                break;
        }
    }

    // Is there something to press right now?
    private bool StatusTileIsActionable() =>
        _updates.State is UpdateState.Available or UpdateState.ReadyToRestart;

    // Is it "live"? This decides whether it renders lit and whether focus may rest on it. Downloading
    // counts deliberately, even though it can't be pressed then: otherwise pressing "download" would
    // drop focus to the tile below and the user would have to climb back up to restart. It is also
    // showing a percentage while downloading, so dimming it would be a lie.
    private bool StatusTileIsLive() =>
        StatusTileIsActionable() || _updates.State is UpdateState.Downloading;

    private int FirstSelectableTile() => StatusTileIsLive() ? StatusTile : 1;

    // The service raises Changed on whichever thread the network task completed on, not the UI thread.
    // Touching Avalonia controls from there takes the app down, so this always bounces back to the UI
    // thread before painting.
    private void OnUpdatesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateSelection();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateSelection);
        }
    }

    private void UpdateSelection()
    {
        var live = StatusTileIsLive();

        UpdStatusText.Text = StatusText();
        UpdTile0.Background = live ? NormalBackground : DisabledBackground;
        UpdStatusText.Foreground = live ? Brushes.White : DisabledForeground;

        // If focus was on the status tile and it stopped being pressable (for example the check
        // finishes as "up to date"), move focus off it: otherwise the ring would sit on a dim tile
        // that answers nothing.
        if (_tile < FirstSelectableTile())
        {
            _tile = FirstSelectableTile();
        }

        for (var i = StatusTile; i <= LastTile; i++)
        {
            var isSelected = i == _tile;
            _tiles[i].Classes.Set("selected", isSelected);
            _rings[i].Classes.Set("selected", isSelected);
            // The status tile's ring must not show while the tile is dim.
            _rings[i].IsVisible = i != StatusTile || live;
        }

        // With no measured text for a tile the gap is left empty rather than filled with something
        // invented: better that it visibly lacks text than that it looks finished and is wrong.
        UpdDescription.Text = Descriptions[_tile] ?? string.Empty;
    }

    // English throughout (UI language rule). Only "No console update available" comes from the
    // reference; the rest are provisional until captures of those states exist.
    private string StatusText() => _updates.State switch
    {
        UpdateState.Idle or UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => "No console update available",
        UpdateState.Available => _updates.AvailableVersion is { } v
            ? $"Console update available ({v})"
            : "Console update available",
        UpdateState.Downloading => $"Downloading update… {_updates.Progress}%",
        UpdateState.ReadyToRestart => "Restart to install update",
        // Own wording, shorter than the service's: its sentence ("Updates are only available when
        // Playfront is installed with its installer.") wraps to three lines and doesn't fit the tile.
        // The long one still goes to the log, which is where it is useful for diagnosis.
        UpdateState.Unsupported => "Updates only work when Playfront is installed",
        // Here the service's own wording is used verbatim: those are short and already explain the
        // cause (no connection, disk full...) instead of hiding it behind a generic "error".
        UpdateState.Failed => _updates.LastError ?? "Couldn't check for updates",
        _ => "Checking for updates…",
    };
}
