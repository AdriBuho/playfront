using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Playfront.App.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Playfront.App.Views;

/// <summary>
/// The "Manage Storage devices" screen (Settings -> System -> Storage devices). Mounted on demand and
/// released on B.
///
/// The four action cards on the left take the ring but do nothing. The cards on the right are built
/// from the machine's REAL fixed drives, read once when the screen opens: name, free space, total
/// size and filesystem, with the donut showing how full each one is.
/// </summary>
public partial class SystemStorageView : UserControl
{
    // Geometry of a device card, measured on the reference. The second and further cards continue
    // down with the same 16 gap the rest of the screen uses; the reference only had one drive.
    private const double CardLeft = 754;
    private const double CardTop = 207;
    private const double CardWidth = 1060;
    private const double CardHeight = 293;
    private const double CardPitch = CardHeight + 16;

    // The donut: a 227 centre-line circle stroked 11, centred 161 in and 146 down from the card's
    // top left corner. Width/Height carry the stroke, hence the +11.
    private const double DonutCentreX = 161;
    private const double DonutCentreY = 146;
    private const double DonutDiameter = 227;
    private const double DonutStroke = 11;

    // Left edge of the text block inside the card, and the ink top of each of its five lines relative
    // to the card. The gap between line 3 and line 4 is the reference's, not a rounding.
    private const double TextLeft = 331;

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

    private readonly Border[] _actions;
    private readonly Border[] _actionRings;
    private readonly List<Border> _deviceRings = new();

    // 0 = the action column, 1 = the device column. Focus starts on the first device, as the
    // reference does.
    private int _column = 1;
    private int _actionIndex;
    private int _deviceIndex;

    /// <summary>Requests closing this screen and returning to Settings (B).</summary>
    public event Action? ExitRequested;

    public SystemStorageView()
    {
        InitializeComponent();

        _actions = new[] { SdAction0, SdAction1, SdAction2, SdAction3 };
        _actionRings = new[] { SdActionRing0, SdActionRing1, SdActionRing2, SdActionRing3 };

        BuildDevices();

        // With no drive readable at all there is nothing to focus on the right.
        if (_deviceRings.Count == 0)
        {
            _column = 0;
        }

        UpdateSelection();
    }

    public void Move(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.Left when _column == 1:
                _column = 0;
                break;
            case GamepadButton.Right when _column == 0 && _deviceRings.Count > 0:
                _column = 1;
                break;
            case GamepadButton.Up when _column == 0 && _actionIndex > 0:
                _actionIndex--;
                break;
            case GamepadButton.Down when _column == 0 && _actionIndex < _actions.Length - 1:
                _actionIndex++;
                break;
            case GamepadButton.Up when _column == 1 && _deviceIndex > 0:
                _deviceIndex--;
                break;
            case GamepadButton.Down when _column == 1 && _deviceIndex < _deviceRings.Count - 1:
                _deviceIndex++;
                break;
            default:
                return; // includes A: nothing here does anything yet
        }

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        for (var i = 0; i < _actions.Length; i++)
        {
            var selected = _column == 0 && i == _actionIndex;
            _actions[i].Classes.Set("selected", selected);
            _actionRings[i].Classes.Set("selected", selected);
        }

        for (var i = 0; i < _deviceRings.Count; i++)
        {
            // Only the ring: the device card keeps #303030 even while selected, which is what the
            // reference shows.
            _deviceRings[i].Classes.Set("selected", _column == 1 && i == _deviceIndex);
        }
    }

    private void BuildDevices()
    {
        foreach (var drive in ReadDrives())
        {
            var index = _deviceRings.Count;
            var top = CardTop + index * CardPitch;

            var card = new Border
            {
                Width = CardWidth,
                Height = CardHeight,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
                Child = BuildCardContent(drive),
            };
            Canvas.SetLeft(card, CardLeft);
            Canvas.SetTop(card, top);
            SdDeviceHost.Children.Add(card);

            var ring = new Border { Width = CardWidth + 16, Height = CardHeight + 16 };
            ring.Classes.Add("selectionRing");
            Canvas.SetLeft(ring, CardLeft - 8);
            Canvas.SetTop(ring, top - 8);
            SdDeviceHost.Children.Add(ring);
            _deviceRings.Add(ring);
        }
    }

    private Canvas BuildCardContent(DriveLine drive)
    {
        var canvas = new Canvas();

        // Track first, used arc on top of it.
        var box = DonutDiameter + DonutStroke;
        var left = DonutCentreX - box / 2;
        var top = DonutCentreY - box / 2;

        var track = new Ellipse
        {
            Width = box,
            Height = box,
            Stroke = TrackBrush,
            StrokeThickness = DonutStroke,
        };
        Canvas.SetLeft(track, left);
        Canvas.SetTop(track, top);
        canvas.Children.Add(track);

        // -90 puts the start at 12 o'clock; the sweep is the USED share, clockwise.
        var used = new Arc
        {
            Width = box,
            Height = box,
            Stroke = Application.Current!.FindResource("AccentBrush") as IBrush ?? Brushes.LimeGreen,
            StrokeThickness = DonutStroke,
            StartAngle = -90,
            SweepAngle = 360 * drive.UsedFraction,
        };
        Canvas.SetLeft(used, left);
        Canvas.SetTop(used, top);
        canvas.Children.Add(used);

        var percent = new TextBlock
        {
            Width = box,
            TextAlignment = TextAlignment.Center,
            FontFamily = (FontFamily)Application.Current!.FindResource("XboxFontFamily")!,
            FontSize = 46,
            Foreground = Brushes.White,
            Text = (drive.UsedFraction * 100).ToString("N1", CultureInfo.InvariantCulture) + "%",
        };
        Canvas.SetLeft(percent, left);
        Canvas.SetTop(percent, 121);
        canvas.Children.Add(percent);

        AddLine(canvas, drive.Name, 53, semibold: true);
        AddLine(canvas, drive.Capacity, 87);
        AddLine(canvas, drive.Format, 123);
        AddLine(canvas, "Drive", 174, semibold: true);
        AddLine(canvas, drive.Root, 209);

        return canvas;
    }

    private static void AddLine(Canvas canvas, string text, double top, bool semibold = false)
    {
        var resource = semibold ? "XboxFontSemibold" : "XboxFontFamily";
        var block = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current!.FindResource(resource)!,
            FontSize = 26,
            Foreground = Brushes.White,
            Text = text,
        };
        Canvas.SetLeft(block, TextLeft);
        Canvas.SetTop(block, top);
        canvas.Children.Add(block);
    }

    private sealed record DriveLine(string Name, string Capacity, string Format, string Root, double UsedFraction);

    // Fixed, ready drives only: removable sticks and network shares come and go, and a card that
    // vanishes mid-navigation is worse than not showing it. The system drive is called "Internal
    // storage" like the reference does; the others use their volume label, or their letter when they
    // have none.
    private static IEnumerable<DriveLine> ReadDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        // Fully qualified from the ROOT: Avalonia.Controls.Shapes also has a Path, and plain
        // "System.IO" resolves to Playfront.App.System, the project's own namespace.
        var systemRoot = global::System.IO.Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        foreach (var drive in drives.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            DriveLine line;
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                var isSystem = string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase);
                var label = isSystem
                    ? "Internal storage"
                    : string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? "Drive " + drive.Name.TrimEnd('\\')
                        : drive.VolumeLabel;

                var free = drive.AvailableFreeSpace;
                var total = drive.TotalSize;

                line = new DriveLine(
                    label,
                    $"{Gigabytes(free)} free of {Gigabytes(total)}",
                    "Formatted as " + drive.DriveFormat,
                    drive.Name,
                    (double)(total - free) / total);
            }
            catch
            {
                // A drive that stops answering mid-read is skipped, not shown half-filled.
                continue;
            }

            yield return line;
        }
    }

    // Gigabytes the way Windows counts them (1024-based), so the numbers match File Explorer.
    private static string Gigabytes(long bytes) =>
        (bytes / 1024d / 1024d / 1024d).ToString("N1", CultureInfo.InvariantCulture) + " GB";
}
