using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Playfront.App.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Playfront.App.Views;

/// <summary>
/// Store CATEGORY page (what Apps > "Music apps" opens): large banner on top, grid of app cards below.
///
/// EVERY number on this screen - coordinates, colours, font sizes - is measured off the reference
/// capture rather than eyeballed. Before nudging a value "because it looks off", assume it was
/// measured.
///
/// Built on demand and released on exit.
/// </summary>
public partial class StoreCategoryView : UserControl
{
    /// <summary>Back to the Store was requested (B).</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// An app's PRODUCT PAGE was requested (A on a card). The value is the artwork file name, which is
    /// how each app is keyed in apps.json. Only fires for cards that have data; A does nothing on the
    /// rest.
    /// </summary>
    public event Action<string>? AppRequested;

    // ===== Grid geometry (measured) =====
    private const double GridLeft = 117;
    private const double GridTop = 583;
    private const double CardW = 256;
    private const double CardH = 352;
    private const double ArtH = 256;   // the artwork is square and full-bleed
    private const double FooterH = 96;
    private const double PitchX = 287.3; // at 288 the sixth card drifts 4 px off
    private const double PitchY = 384.3;
    private const int Cols = 6;
    private const int Rows = 2;

    // Footer tint. Measurement gave #2D3234 at 67%; rendered against the capture that came out 3-4
    // levels dark across the whole row, so the tint is lightened just enough (same alpha).
    private static readonly IBrush FooterFill = new SolidColorBrush(Color.Parse("#AB313639"));
    private static readonly IBrush Placeholder = new SolidColorBrush(Color.Parse("#242A2D"));

    // One app's data, read from Assets/Icons/Store/Apps/apps.json. The text is EXACTLY what the Xbox
    // Store shows, not what the PC Store catalogue returns: title, rating, vote count and description
    // all differ between the two, and this screen has to match the Xbox one.
    //
    // "Tint" is the colour the whole page takes when that app is focused; both it and the band colour
    // derive from the artwork itself (PNG average x 0.36 and x 0.26).
    //
    // Entries are added ONE AT A TIME, each from its own capture - do not fill the rest in advance.
    //
    // The last three fields (publisher and age rating) are NOT used on this page: they belong to the
    // PRODUCT PAGE (StoreAppView), which reads the same apps.json. They live here to avoid two data
    // files holding the same thing. Optional: an app with no product-page data leaves them empty
    // rather than having them invented.
    internal sealed record AppInfo(
        string File, string Title, string Genre, string Price, bool Owned,
        // NO review score, NO vote count and NO friends-who-own-this count. All three were numbers
        // nobody had, and a store page inventing them is claiming things about the product and about
        // the user's friends. See the notes where each used to be drawn.
        string Description, string Tint,
        string? Publisher = null, string? Esrb = null, string? EsrbNotes = null,
        // Product page, DETAILS section. ReleaseDate is the publisher's, not ours. Description stays
        // the one-liner shown on the first section; LongDescription is the paragraph on the details
        // card. Features is the short bulleted list the console puts in a card of its own - labels
        // only, a handful of words each.
        string? ReleaseDate = null, string? LongDescription = null,
        IReadOnlyList<string>? Features = null);

    internal static readonly Dictionary<string, AppInfo> Apps = LoadApps();

    private static Dictionary<string, AppInfo> LoadApps()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://Playfront.App/Assets/Icons/Store/Apps/apps.json"));
            var list = JsonSerializer.Deserialize<List<AppInfo>>(s, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            return list?.ToDictionary(a => a.File, a => a) ?? new Dictionary<string, AppInfo>();
        }
        catch
        {
            return new Dictionary<string, AppInfo>(); // no data: the page still works
        }
    }

    // One card: its artwork (a file under Assets/Icons/Store/Apps, or null when we do not have it yet)
    // and the footer label. Row 2 and the 6th of row 1 are still pending: the capture crops them or
    // they are not identifiable, so that artwork comes later.
    private sealed record Card(string? Art, string Label, bool Owned);

    /// <summary>
    /// What a category page is made of. Same layout for all of them - the console uses one page for
    /// every subcategory of Apps - so only the contents change.
    /// </summary>
    /// <param name="Banner">
    /// False leaves the page with no promo box at the top and the selection starting on the first
    /// card. Not every subcategory has one: measured on the console, "Apps for gamers" goes straight
    /// from the title to the product detail.
    /// </param>
    /// <param name="Header">
    /// The line at the top left. NOT the subcategory's name: on the console the two are different
    /// texts - "Entertainment apps" is headed "Top entertainment apps for you".
    /// </param>
    private sealed record Category(string Header, Card[] Cards, bool Banner);

    private static readonly Dictionary<string, Category> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        // Several read "Owned" in the Store capture, from that account. Here they are ALL "Free": no
        // tick, text against the edge, like the others.
        ["Music apps"] = new("Best music apps", new[]
        {
            new Card("apple-music.png",  "Free",  false),
            new Card("youtube.png",      "Free",  false),
            new Card("dolby-access.png", "Free",  false),
            new Card("spotify-black.png", "Free", false),
            new Card("pandora.png",      "Free",  false),
            new Card(null,               "Free",  false),
        }, Banner: true),

        // OURS, not the console's: a PC store needs the launchers, and Steam is the only one so far.
        ["Launchers"] = new("Launchers", new[]
        {
            new Card("steam.png", "Free", false),
        }, Banner: false),
    };

    private Card[] _cards = Array.Empty<Card>();

    // ===== Focus =====
    // 0 = banner; 1.. = cards by row (row 1 = 1..6, row 2 = 7..12).
    private int _focus;
    private readonly List<Border> _cardRings = new();
    private readonly List<Border> _cardRingsInner = new();
    private readonly List<Border> _cardVeils = new();

    public StoreCategoryView(string category)
    {
        InitializeComponent();

        var data = Categories.TryGetValue(category, out var found)
            ? found
            : new Category(category, Array.Empty<Card>(), Banner: false);
        _cards = data.Cards;
        DetailCategory.Text = data.Header;
        BannerTitle.Text = data.Header;

        BannerIcon0.Source = LoadArt("pandora.png");
        BannerIcon1.Source = LoadArt("spotify-black.png");
        BannerIcon2.Source = LoadArt("amazon-music-zoom.png");

        // With no banner there is nothing at index 0 to land on, so the selection starts on the first
        // card. The box itself needs no hiding: it is only drawn while index 0 has the focus, and
        // without a banner the focus never goes there.
        _hasBanner = data.Banner;
        _focus = _hasBanner ? 0 : 1;

        BuildGrid();
        UpdateSelection();
    }

    private readonly bool _hasBanner;

    private static Bitmap? LoadArt(string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return null;
        }

        try
        {
            return new Bitmap(AssetLoader.Open(new Uri($"avares://Playfront.App/Assets/Icons/Store/Apps/{file}")));
        }
        catch
        {
            return null; // missing file: the card keeps its dark box
        }
    }

    // Builds the 12 cards: square full-bleed artwork plus a translucent footer with its label, all
    // clipped by the container's rounded corners.
    private void BuildGrid()
    {
        for (var i = 0; i < Cols * Rows; i++)
        {
            var row = i / Cols;
            var col = i % Cols;
            var x = GridLeft + col * PitchX;
            var y = GridTop + row * PitchY;
            var data = CardAt(i);

            // The card has NO background of its own. The footer is translucent and what has to show
            // through is the PAGE background - which is why in the reference the footer grows greener
            // towards the right. An opaque card background would blend with it and every footer would
            // come out the same colour.
            var card = new Border
            {
                Width = CardW,
                Height = CardH,
                CornerRadius = new CornerRadius(9),
                ClipToBounds = true,
                Background = Brushes.Transparent,
                BoxShadow = BoxShadows.Parse("0 2 8 -2 #80000000"),
            };
            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, y);

            var inner = new Canvas();
            card.Child = inner;

            var art = LoadArt(data.Art);
            if (art is not null)
            {
                inner.Children.Add(new Image
                {
                    Source = art,
                    Width = CardW,
                    Height = ArtH,
                    Stretch = Stretch.UniformToFill,
                });
            }
            else
            {
                // No artwork yet (the 6th of row 1 and all of row 2). Dark box over the ARTWORK area
                // only, not the whole card, so the translucent footer is not covered.
                var box = new Rectangle { Width = CardW, Height = ArtH, Fill = Placeholder };
                inner.Children.Add(box);
            }

            // Footer: translucent DARK tint (#2D3234 at 67%), NOT a white veil - what shows through is
            // the page background, so the footer grows greener towards the right.
            var footer = new Border
            {
                Width = CardW,
                Height = FooterH,
                Background = FooterFill,
            };
            Canvas.SetTop(footer, ArtH);
            inner.Children.Add(footer);

            // Footer content placed at absolute coordinates: automatic centring lands it ~4.7 px lower
            // than measured.
            var textLeft = data.Owned ? 54d : 16d;
            var label = new TextBlock
            {
                Text = data.Label,
                FontFamily = new FontFamily("Segoe UI"), // see the font note in the XAML
                FontSize = 24,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
            };
            Canvas.SetLeft(label, textLeft);
            Canvas.SetTop(label, ArtH + 28);
            inner.Children.Add(label);

            if (data.Owned)
            {
                inner.Children.Add(BuildOwnedIcon(16, ArtH + 33));
            }

            // ~8% black veil: in the Store, UNfocused cards sit at 92% brightness. Doing it this way
            // leaves the PNGs untouched and the effect reverses on focus.
            var veil = new Border
            {
                Width = CardW,
                Height = CardH,
                Background = new SolidColorBrush(Color.Parse("#14000000")),
                IsHitTestVisible = false,
            };
            inner.Children.Add(veil);
            _cardVeils.Add(veil);

            GridHost.Children.Add(card);

            // CARD focus ring. Measured: thinner than the banner's (accent stroke ~3.4 and black band
            // ~3.4, not 6 and 5), floats 6-7 px off the card, and its radii grow outwards (7 artwork,
            // 9 band, 13 accent).
            var ringInner = new Border
            {
                Width = CardW + 8,
                Height = CardH + 8,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(3.4),
            };
            ringInner.Classes.Add("catRingInner");
            Canvas.SetLeft(ringInner, x - 4);
            Canvas.SetTop(ringInner, y - 4);
            GridHost.Children.Add(ringInner);
            _cardRingsInner.Add(ringInner);

            var ring = new Border
            {
                Width = CardW + 14,
                Height = CardH + 14,
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(3.4),
            };
            ring.Classes.Add("catRing");
            Canvas.SetLeft(ring, x - 7);
            Canvas.SetTop(ring, y - 7);
            GridHost.Children.Add(ring);
            _cardRings.Add(ring);
        }
    }

    // "Owned" icon: thin white circle with a tick of the same weight inside. 24x24 box; the long arm
    // rises at exactly 45 degrees and its tip grazes the inner face of the circle.
    private static Canvas BuildOwnedIcon(double left, double top)
    {
        var box = new Canvas { Width = 24, Height = 24 };
        var ring = new Ellipse
        {
            Width = 22.2,
            Height = 22.2,
            Stroke = Brushes.White,
            StrokeThickness = 1.8,
        };
        Canvas.SetLeft(ring, 0.9);
        Canvas.SetTop(ring, 0.9);
        box.Children.Add(ring);
        box.Children.Add(new Path
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M 5.2,11.5 L 9.2,17.3 L 19.7,6.0"),
        });
        Canvas.SetLeft(box, left);
        Canvas.SetTop(box, top);
        return box;
    }

    public void Move(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.A when _focus > 0:
            {
                // Only cards with data in apps.json open a product page. A does nothing on the rest.
                var card = CardAt(_focus - 1).Art;
                if (card is not null && Apps.ContainsKey(card))
                {
                    AppRequested?.Invoke(card);
                }

                return;
            }

            case GamepadButton.Down:
                // Down from the banner lands on the first card of row 1; between rows the column is
                // kept.
                if (_focus == 0)
                {
                    _focus = 1;
                }
                else if (_focus <= Cols)
                {
                    _focus += Cols;
                }

                break;
            case GamepadButton.Up:
                if (_focus > Cols)
                {
                    _focus -= Cols;
                }
                else if (_focus >= 1 && _hasBanner)
                {
                    _focus = 0; // back to the banner
                }

                break;
            case GamepadButton.Left:
                // Left in the first column does nothing (there is no sidebar on this page).
                if (_focus > 1 && (_focus - 1) % Cols != 0)
                {
                    _focus--;
                }

                break;
            case GamepadButton.Right:
                if (_focus >= 1 && _focus % Cols != 0 && _focus < Cols * Rows)
                {
                    _focus++;
                }

                break;
            default:
                return;
        }

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var onBanner = _focus == 0;
        BannerRing.Classes.Set("selected", onBanner);
        BannerRingInner.Classes.Set("selected", onBanner);

        for (var i = 0; i < _cardRings.Count; i++)
        {
            var on = _focus == i + 1;
            _cardRings[i].Classes.Set("selected", on);
            _cardRingsInner[i].Classes.Set("selected", on);
            _cardVeils[i].IsVisible = !on; // the focused card gets its full brightness back
        }

        // Top block state: focus on the banner shows the banner; focus on a card removes the banner
        // entirely (not dimmed - gone) and puts the app details, the top-right hints and the tinted
        // background in its place.
        var art = onBanner ? null : CardAt(_focus - 1).Art;
        var info = art is not null && Apps.TryGetValue(art, out var found) ? found : null;

        BannerBox.IsVisible = onBanner;
        DetailLayer.IsVisible = !onBanner;
        TopHints.IsVisible = !onBanner;

        // The background is the ONLY thing that does not switch instantly: it cross-fades (SetAmbient).
        SetAmbient(info, art, onBanner);

        if (onBanner)
        {
            return;
        }

        ShowDetail(info, art);
    }

    // A slot with nothing in it: dark box, no artwork, no data. A category with one product leaves
    // eleven of these, which is what the grid is for.
    private static readonly Card Empty = new(null, "Free", false);

    private Card CardAt(int index)
    {
        var row = index / Cols;
        var col = index % Cols;
        return row == 0 && col < _cards.Length ? _cards[col] : Empty;
    }

    // Fills the detail block with the focused app's data and tints the background with its colour. For
    // cards with no data yet, the block is left blank and the background neutral grey, rather than
    // leaving the previous app's details on screen.
    private void ShowDetail(AppInfo? info, string? art)
    {
        DetailTitle.Text = info?.Title ?? string.Empty;
        DetailDescription.Text = info?.Description ?? string.Empty;
        GenreText.Text = info?.Genre ?? string.Empty;
        GenrePill.IsVisible = !string.IsNullOrEmpty(info?.Genre);

        // Status line: "Owned" with its icon when the app is owned, otherwise the price.
        var owned = info?.Owned == true;
        OwnedIconHost.IsVisible = owned;
        OwnedIconHost.Children.Clear();
        if (owned)
        {
            var icon = BuildCheckIcon(35, 2.6);
            // Measured: icon and text share a vertical centre. 7.5 rather than 9, which left the icon
            // slightly low against the reference.
            Canvas.SetTop(icon, 7.5);
            OwnedIconHost.Children.Add(icon);
            DetailPrice.Text = "Owned";
            Canvas.SetLeft(DetailPrice, 160); // room for the icon (measured 12 px gap)
        }
        else
        {
            DetailPrice.Text = info?.Price ?? string.Empty;
            Canvas.SetLeft(DetailPrice, 114);
        }

        // The status line sits under the LAST line of the description, not at a fixed height: with one
        // line it rises 49 px compared to two.
        var lines = string.IsNullOrEmpty(info?.Description) ? 0 : DescriptionLines(info.Description);
        Canvas.SetTop(StatusRow, 300 + Math.Max(0, lines - 1) * 35 + 38);

        // Apps without data do not get half a screen: pills and artwork box are hidden and the
        // background stays neutral.
        PillRow.IsVisible = info is not null;
        DetailArtBox.IsVisible = info is not null;

        var bitmap = LoadArt(art);
        DetailArt.Source = bitmap;
        DetailArtBlur.Source = bitmap; // side bands are the same artwork, blurred
    }

    // ===== Ambient background, cross-faded between apps =====
    // Two identical layers: the bottom one (_ambBase) is always at 100% with the background currently
    // on screen, and the top one (_ambTop) fades 0 -> 100 with the new one. On the next change the
    // roles swap and a fresh fade starts. Done this way - rather than fading one out while fading the
    // other in - because during that crossover both would be semi-transparent at once and the page's
    // purple background would show through.
    private sealed record AmbientLayers(Canvas Root, Rectangle Fill, Image Art);

    private AmbientLayers? _ambBase, _ambTop;
    private bool _ambientShown;

    private void SetAmbient(AppInfo? info, string? art, bool onBanner)
    {
        _ambBase ??= new AmbientLayers(AmbientA, AmbientFillA, AmbientArtA);
        _ambTop ??= new AmbientLayers(AmbientB, AmbientFillB, AmbientArtB);

        // On the banner there is no app background: fade to nothing and let the page's show.
        if (onBanner)
        {
            AmbientHost.Opacity = 0;
            _ambientShown = false;
            return;
        }

        (_ambBase, _ambTop) = (_ambTop, _ambBase);
        SetOpacityNow(_ambBase.Root, 1); // any fade in flight is finished off here
        SetOpacityNow(_ambTop.Root, 0);
        _ambBase.Root.ZIndex = 0;
        _ambTop.Root.ZIndex = 1;

        FillAmbient(_ambTop, info, art);

        if (_ambientShown)
        {
            _ambTop.Root.Opacity = 1; // cross-fade from the previous app to the new one
        }
        else
        {
            SetOpacityNow(_ambTop.Root, 1); // coming from the banner: the host does the fade
        }

        AmbientHost.Opacity = 1;
        _ambientShown = true;
    }

    // Paints one of the two layers with an app's background: flat colour MEASURED from its capture (not
    // computed) plus the artwork blown up and blurred, centred on the focused card, which is what the
    // real Store does.
    private void FillAmbient(AmbientLayers layer, AppInfo? info, string? art)
    {
        layer.Fill.Fill = new SolidColorBrush(Color.Parse(info?.Tint ?? "#2A2A2A"));

        // The wash is the PRE-DARKENED artwork (*-ambient.png). Measured: its X centre lines up with
        // the centre of that card's artwork. It is not a small blob - it covers the WHOLE screen. With
        // uniform artwork (YouTube, white) the background comes out flat and only the logo shows near
        // its card; with varied artwork (Dolby's teal-blue-purple gradient) the whole background varies
        // the same way the artwork does, which is exactly what those captures show.
        var ambient = LoadArt(art?.Replace(".png", "-ambient.png"));
        layer.Art.Source = ambient;
        layer.Art.IsVisible = ambient is not null;
        if (ambient is not null)
        {
            const double coverW = 2600, coverH = 2600;
            var col = (_focus - 1) % Cols;
            var centerX = GridLeft + col * PitchX + CardW / 2;
            layer.Art.Width = coverW;
            layer.Art.Height = coverH;
            Canvas.SetLeft(layer.Art, centerX - coverW / 2);
            Canvas.SetTop(layer.Art, 540 - coverH / 2);
        }
    }

    // Sets opacity WITHOUT a fade (suppressing the transition for an instant), to place a layer's
    // starting state before animating it.
    private static void SetOpacityNow(Control control, double value)
    {
        var transitions = control.Transitions;
        control.Transitions = null;
        control.Opacity = value;
        control.Transitions = transitions;
    }

    // How many lines the description takes at the block's width. Really measured, not estimated from
    // character count, so the status line lands where it should.
    private int DescriptionLines(string text)
    {
        DetailDescription.Text = text;
        DetailDescription.Measure(new Size(DetailDescription.Width, double.PositiveInfinity));
        return Math.Max(1, (int)Math.Round(DetailDescription.DesiredSize.Height / 35));
    }

    // Circle-with-tick icon, the same one as the card footers but larger.
    private static Canvas BuildCheckIcon(double size, double stroke)
    {
        var k = size / 24.0;
        var box = new Canvas { Width = size, Height = size };
        var ring = new Ellipse
        {
            Width = 22.2 * k,
            Height = 22.2 * k,
            Stroke = Brushes.White,
            StrokeThickness = stroke,
        };
        Canvas.SetLeft(ring, 0.9 * k);
        Canvas.SetTop(ring, 0.9 * k);
        box.Children.Add(ring);
        var tick = new Path
        {
            Stroke = Brushes.White,
            StrokeThickness = stroke,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M 5.2,11.5 L 9.2,17.3 L 19.7,6.0"),
            RenderTransform = new ScaleTransform(k, k),
            RenderTransformOrigin = RelativePoint.TopLeft,
        };
        box.Children.Add(tick);
        return box;
    }

    // The star rating that used to sit beside the genre pill is gone, drawing code included. It was
    // fed by numbers nobody had measured, and a score is not decoration: five stars next to a product
    // is a claim about what people think of it. If real review data ever arrives, the pill goes back
    // where the reference has it, to the left of the genre one.
}
