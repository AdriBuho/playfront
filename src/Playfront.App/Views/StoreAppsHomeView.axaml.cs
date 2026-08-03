using System;
using Playfront.App.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Playfront.App.Views;

/// <summary>
/// Store "Apps Home" page. Layout and the fixed pieces live in the XAML; the rows of app tiles are
/// built here because they are data, not markup.
///
/// NOT NAVIGABLE on purpose: nothing takes focus and A does nothing. B goes back to the Store. The
/// wheel scrolls, because the page is taller than the screen.
///
/// Built on entry and released on exit, like the other store pages, so its artwork is not held while
/// the user is elsewhere.
/// </summary>
public partial class StoreAppsHomeView : UserControl
{
    /// <summary>Back to the Store was requested (B).</summary>
    public event Action? ExitRequested;

    // ===== Row geometry (measured) =====
    private const double RowLeft = 256;      // same left edge as everything else on the page
    private const double TileSize = 256;     // the tiles are square
    private const double TilePitch = 288;    // 976 -> 1264 between two tiles in the reference
    private const double LabelBandH = 96;    // the strip under each tile that holds Owned/Free/price
    private const double TitleToTiles = 74;  // from the section title's top to the top of its tiles
    private const double RowPitch = 520;     // from one section title to the next

    private const double FirstTitleTop = 936;

    /// <summary>One tile: the artwork, and what the store writes underneath it.</summary>
    private sealed record Tile(string Art, string Label);

    private sealed record Row(string Title, Tile[] Tiles);

    // EXACTLY what the console shows, in the order it shows it. Where a logo already exists in the
    // project it is used instead of the crop - a vendor's own file is sharper than any capture.
    private static readonly Row[] Rows_ =
    {
        new("Top entertainment apps for you", new[]
        {
            new Tile("Store/Apps/youtube.png",              "Owned"),
            new Tile("Store/AppsHome/app-netflix.png",      "Owned"),
            new Tile("Store/AppsHome/app-disney-plus.png",  "Owned"),
            new Tile("Store/AppsHome/app-hbo-max.png",      "Free"),
            new Tile("Store/Apps/spotify.png",              "Owned"),
        }),
        new("Apps for gamers", new[]
        {
            new Tile("Store/Apps/apple-music.png",       "Free"),
            new Tile("Store/AppsHome/app-nitrado.png",   "Free"),
            new Tile("Store/AppsHome/app-dolby.png",     "Free"),
            new Tile("Store/AppsHome/app-medal.png",     "View product"),
            new Tile("Store/Apps/amazon-music.png",      "Free"),
        }),
    };

    public StoreAppsHomeView()
    {
        InitializeComponent();
        BuildRows();
    }

    private void BuildRows()
    {
        for (var r = 0; r < Rows_.Length; r++)
        {
            var top = FirstTitleTop + r * RowPitch;

            var title = new TextBlock
            {
                Text = Rows_[r].Title,
                FontFamily = (FontFamily)Application.Current!.FindResource("XboxFontDisplaySemibold")!,
                FontSize = 40,
                Foreground = Brushes.White,
                Opacity = 0.62,
            };
            Canvas.SetLeft(title, RowLeft);
            Canvas.SetTop(title, top);
            Rows.Children.Add(title);

            for (var i = 0; i < Rows_[r].Tiles.Length; i++)
            {
                AddTile(Rows_[r].Tiles[i], RowLeft + i * TilePitch, top + TitleToTiles);
            }
        }
    }

    private void AddTile(Tile tile, double left, double top)
    {
        // Art on top, label band underneath, both inside one rounded box - the same shape the rest of
        // the store uses for an app.
        var art = new Image
        {
            Width = TileSize,
            Height = TileSize,
            Stretch = Stretch.UniformToFill,
            Source = Load(tile.Art),
        };
        RenderOptions.SetBitmapInterpolationMode(art, BitmapInterpolationMode.HighQuality);

        var label = new TextBlock
        {
            Text = tile.Label,
            FontFamily = (FontFamily)Application.Current!.FindResource("XboxFontSemibold")!,
            FontSize = 25,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0),
        };

        var band = new Border
        {
            Height = LabelBandH,
            Background = new SolidColorBrush(Color.Parse("#AB313639")),
            Child = label,
        };

        var stack = new StackPanel();
        stack.Children.Add(art);
        stack.Children.Add(band);

        var box = new Border
        {
            Width = TileSize,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#242A2D")),
            Child = stack,
        };

        Canvas.SetLeft(box, left);
        Canvas.SetTop(box, top);
        Rows.Children.Add(box);
    }

    // A missing file must not take the page down: the heavy art is optional by design, so a tile
    // simply stays as its placeholder colour.
    private static Bitmap? Load(string relative)
    {
        try
        {
            return new Bitmap(AssetLoader.Open(new Uri($"avares://Playfront.App/Assets/Icons/{relative}")));
        }
        catch (Exception e)
        {
            CrashLog.Log($"Apps Home: could not load {relative}", e);
            return null;
        }
    }

    /// <summary>
    /// Gamepad. The page is not navigable yet, so only B does anything; the sticks and d-pad scroll,
    /// which is the one thing that would otherwise leave the lower rows unreachable with a pad.
    /// </summary>
    public void Move(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                break;
            case GamepadButton.Down:
                Scroll.LineDown();
                break;
            case GamepadButton.Up:
                Scroll.LineUp();
                break;
        }
    }
}
