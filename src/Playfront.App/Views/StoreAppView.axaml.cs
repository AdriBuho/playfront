using System;
using System.Collections.Generic;
using System.Globalization;
using Playfront.App.Input;
using Playfront.App.Library;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Playfront.App.Views;

/// <summary>
/// Store PRODUCT PAGE: opened by pressing A on a card in the category page. Large artwork on the
/// right, app details on the left, install button and age-rating block bottom right.
///
/// Every measurement on this screen is taken from a real Store capture; the detail lives in the XAML.
///
/// Built on demand and released on exit.
/// </summary>
public partial class StoreAppView : UserControl
{
    /// <summary>Back to the category page was requested (B).</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// The primary button was pressed (A on INSTALL/PLAY). MainWindow decides what that means per app;
    /// for YouTube it installs it if needed and launches it. The value is the artwork file that
    /// identifies the app in apps.json.
    /// </summary>
    public event Action<string>? ActionInvoked;

    private readonly string _art;

    // Only two navigable elements: the primary button and the wishlist button.
    private const int ActionButton = 0;
    private const int WishButton = 1;

    private int _focus = ActionButton;

    // Which section of the product page is on screen: 0 = Overview, 1 = Details. The console has four
    // (Ratings and reviews, Screenshots follow); only these two exist here, so the indicator shows
    // two dots and neither footer names a section that cannot be reached.
    private int _section;

    // Which card of section 2 has focus: 0 = About, 1 = Description.
    private const int AboutCard = 0;
    private const int DescriptionCard = 1;
    private const int FeaturesCard = 2;

    /// <summary>How far the row travels per card. Measured: 688 wide plus a 32 gutter.</summary>
    private const double CardPitch = 720;

    // How many cards this product actually has. The last one only exists when the catalogue lists
    // features, so a product without them must not let the selection walk onto an empty slot.
    private int _cardCount = 2;

    private int _card = AboutCard;

    // Acquired dialog. While it is up it takes every button: it covers the whole screen, so letting
    // the page underneath still react would move things nobody can see.
    private bool _dialogOpen;
    private int _dialogButton;
    private const int DialogButtons = 2;

    public StoreAppView(string art)
    {
        InitializeComponent();

        _art = art;

        // Artwork is shown at 701 px here, far larger than on the category card. The normal file is
        // 300 px and looked blocky scaled up, so a "-large" variant (514 px) is preferred when one
        // exists. Otherwise the normal one.
        Art.Source = LoadArt(art?.Replace(".png", "-large.png")) ?? LoadArt(art);

        // A product with a full-bleed image uses it as the whole background and drops the art box; one
        // without keeps the box over the plain backdrop. The console does the same, and the difference
        // is the product, not the layout: Spotify publishes a 1920x1080 image, YouTube only a square.
        var hero = LoadArt(art?.Replace(".png", "-hero.png"));
        Hero.Source = hero;
        Hero.IsVisible = hero is not null;
        HeroScrim.IsVisible = hero is not null;
        HeroWash.IsVisible = hero is not null;
        HeroLeft.IsVisible = hero is not null;

        // The part of the artwork that keeps its full brightness, if the product has one.
        var top = hero is null ? null : LoadArt(art?.Replace(".png", "-hero-top.png"));
        HeroTop.Source = top;
        HeroTop.IsVisible = top is not null;
        ArtBox.IsVisible = hero is null;

        // Details come from apps.json, the same file the category page reads.
        StoreCategoryView.Apps.TryGetValue(art, out var info);

        TitleText.Text = info?.Title ?? string.Empty;

        // Publisher line: "<publisher> ▪ <genre>". apps.json stores the genre uppercased because the
        // category page draws it as a pill; here it reads as a normal word, so it is title-cased.
        var genre = Capitalize(info?.Genre);
        var publisher = info?.Publisher;
        PublisherText.Text = string.IsNullOrEmpty(publisher)
            ? genre
            : string.IsNullOrEmpty(genre) ? publisher : $"{publisher}  ▪  {genre}";

        DescriptionText.Text = info?.Description ?? string.Empty;

        // Primary button. Three steps, the same as the console: GET puts it in the library without
        // bringing it down, INSTALL brings it down, PLAY runs it. What the button says depends on
        // where this product already is in the user's library, which is persisted.
        RefreshAction();

        // Age rating. Apps with no rating data leave the whole block unpainted - better empty than
        // invented.
        var esrb = info?.Esrb;
        var hasRating = !string.IsNullOrEmpty(esrb);
        RatingLabel.Text = esrb ?? string.Empty;
        RatingNotes.Text = info?.EsrbNotes ?? string.Empty;
        // The seal is an image, one file per rating, not something drawn. Only TEEN exists so far.
        RatingSeal.Source = hasRating ? LoadIcon($"esrb-{esrb!.ToLowerInvariant()}.png") : null;
        RatingSeal.IsVisible = RatingSeal.Source is not null;
        RatingRule.IsVisible = hasRating;

        FillDetails(info, art);
        FillDialog(info, art);

        UpdateSelection();
    }

    /// <summary>
    /// Section 2 (Details). Anything apps.json does not carry is left blank rather than filled with a
    /// plausible-looking value.
    /// </summary>
    private void FillDetails(StoreCategoryView.AppInfo? info, string art)
    {
        S2Title.Text = info?.Title ?? string.Empty;
        S2Icon.Source = LoadArt(art);

        S2Publisher.Text = info?.Publisher ?? string.Empty;
        S2Release.Text = info?.ReleaseDate ?? string.Empty;
        S2Category.Text = Capitalize(info?.Genre);
        S2Rating.Text = info?.Esrb ?? string.Empty;
        S2RatingNotes.Text = info?.EsrbNotes ?? string.Empty;

        // The full store text if there is one; otherwise the one-liner the first section shows, which
        // is at least true, instead of an empty card.
        S2Description.Text = info?.LongDescription ?? info?.Description ?? string.Empty;

        FillFeatures(info?.Features);
        ResetDescriptionScroll();

        // The details page is built out of the PRODUCT'S artwork, blurred: the page behind and all
        // three cards share one picture, each offset to its own position so it runs on across them
        // rather than restarting inside each. The hero is preferred when there is one - it fills the
        // frame - and the square icon stands in when there is not.
        var backdrop = LoadArt(art?.Replace(".png", "-hero.png"))
                       ?? LoadArt(art?.Replace(".png", "-large.png"))
                       ?? LoadArt(art);
        S2BgArt.Source = backdrop;
        S2ArtAbout.Source = backdrop;
        S2ArtDesc.Source = backdrop;
        S2ArtFeat.Source = backdrop;

        // The tint under it comes from the catalogue too. It used to be one hardcoded colour, and the
        // colour was YouTube's, so every other product's details page came out wearing it.
        if (info?.Tint is { Length: > 0 } tint && Color.TryParse(tint, out var color))
        {
            S2BgFill.Fill = new SolidColorBrush(color);
        }

        // SIZE. Deliberately NOT the console's number - that is Microsoft's build of the app, not
        // what this costs here. Once the product has been run its own folder is MEASURED; before
        // that it is the catalogue's estimate, and it says "About" so the two cannot be confused.
        //
        // "Nothing to download" was wrong and is worth remembering why: Playfront fetching no
        // installer is not the same as the product costing nothing. The embedded browser pulls its
        // own components down on first run - 118 MB measured for YouTube.
        var product = LibraryCatalog.ForArt(art);
        if (product is null)
        {
            S2Size.Text = string.Empty;
        }
        else
        {
            var actual = LibraryCatalog.SizeOnDisk(product);
            S2Size.Text = actual is not null
                ? FormatSize(actual.Value)
                : $"About {FormatSize(product.ApproxBytes)}";
        }
    }

    /// <summary>
    /// Acquired dialog. Same artwork as the page, plus the pre-darkened copy behind it.
    /// </summary>
    private void FillDialog(StoreCategoryView.AppInfo? info, string art)
    {
        DlgArt.Source = LoadArt(art?.Replace(".png", "-large.png")) ?? LoadArt(art);
        DlgAmbient.Source = LoadArt(art?.Replace(".png", "-ambient.png"));

        var title = info?.Title;
        DlgBody.Text = string.IsNullOrEmpty(title)
            ? string.Empty
            : $"You can check install status of {title} in My games & apps";
    }

    // InvariantCulture on purpose: the interface is in English, and on a Spanish Windows the default
    // formatting writes "118,8 MB", which reads as a thousands separator in an English UI.
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        var mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", mb / 1024.0)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", mb);
    }

    /// <summary>
    /// Puts the right word on the primary button for where this product stands: not in the library
    /// yet (GET), in it but not brought down (INSTALL), or ready (PLAY). Called again after the
    /// button is pressed so the page reflects what just happened without being rebuilt.
    /// </summary>
    /// <summary>An install is running right now. Set by MainWindow, which owns the install.</summary>
    public bool Installing { get; set; }

    /// <summary>Why the last install did not finish, or null. Shown instead of inventing success.</summary>
    public string? InstallError { get; set; }

    public void RefreshAction()
    {
        StoreCategoryView.Apps.TryGetValue(_art, out var info);
        var catalogo = LibraryCatalog.ForArt(_art);
        var enBiblioteca = catalogo is not null ? LibraryStore.Find(catalogo.Id) : null;

        // An external Windows program: the file on disk is the truth, not the library file. A user
        // who uninstalls it from Windows must see GET again, not a PLAY that does nothing.
        var externa = catalogo is not null ? LibraryCatalog.ExternalFor(catalogo.Id) : null;
        if (externa is not null)
        {
            if (Installing)
            {
                ActionText.Text = "INSTALLING";
                ActionSubText.Text = "Downloading…";
            }
            else if (externa.IsInstalled)
            {
                ActionText.Text = "LAUNCH APP";
                ActionSubText.Text = "You own this";
            }
            else
            {
                ActionText.Text = enBiblioteca is null ? "GET" : "INSTALL";
                ActionSubText.Text = InstallError ?? (enBiblioteca is null
                    ? info?.Price ?? string.Empty
                    : "In your library");
            }

            LayoutActionButton();
            return;
        }

        if (catalogo is null)
        {
            // Playfront does not provide this product yet: nothing to get, so the page stays as it
            // was rather than offering a button that would do nothing.
            ActionText.Text = "INSTALL";
            ActionSubText.Text = info?.Price ?? string.Empty;
            return;
        }

        if (enBiblioteca is null)
        {
            ActionText.Text = "GET";
            ActionSubText.Text = info?.Price ?? string.Empty;
        }
        else if (enBiblioteca.State == LibraryState.Owned)
        {
            ActionText.Text = "INSTALL";
            ActionSubText.Text = "In your library";
        }
        else
        {
            // Wording taken from the console: an app says LAUNCH APP, a game says PLAY, and the
            // second line is about ownership, not about where the file is.
            ActionText.Text = catalogo.Kind == LibraryKind.Game ? "PLAY" : "LAUNCH APP";
            ActionSubText.Text = "You own this";
        }

        LayoutActionButton();
    }

    /// <summary>
    /// Sizes the primary button to its label and slides the wishlist button along behind it.
    /// </summary>
    private void LayoutActionButton()
    {
        // The console grows this button: 191 wide for "GET / Free", 220 for "LAUNCH APP / You own
        // this" - measured on both. Text inset 34 on the left and 33 on the right, and 191 is a
        // FLOOR: a short label does not shrink the button below it.
        const double MinWidth = 191, InsetLeft = 34, InsetRight = 33, Gap = 17, Left = 112;

        ActionText.Measure(Size.Infinity);
        ActionSubText.Measure(Size.Infinity);
        var texto = Math.Max(ActionText.DesiredSize.Width, ActionSubText.DesiredSize.Width);
        var ancho = Math.Max(MinWidth, Math.Ceiling(InsetLeft + texto + InsetRight));

        ActionBg.Width = ancho;
        ActionRing.Width = ancho + 16;
        ActionRingInner.Width = ancho + 8;

        var wish = Left + ancho + Gap;
        Canvas.SetLeft(WishBg, wish);
        Canvas.SetLeft(WishRing, wish - 8);
        Canvas.SetLeft(WishRingInner, wish - 4);
        Canvas.SetLeft(WishIcon, wish + 34); // same inset the heart already had inside its button
    }

    // One-off Store icons (the rating seal, for now).
    private static Bitmap? LoadIcon(string file)
    {
        try
        {
            return new Bitmap(AssetLoader.Open(new Uri($"avares://Playfront.App/Assets/Icons/Store/{file}")));
        }
        catch
        {
            return null; // no seal for that rating: leave the block without one rather than invent it
        }
    }

    // ===== DESCRIPTION SCROLL =====
    // How far the text has been run up, and how far it can go. Both in the card's own pixels.
    private double _descScroll;
    private double _descMax;

    /// <summary>Visible height of the clipped area, kept next to the XAML value it mirrors.</summary>
    private const double DescViewport = 520;

    // How hard the stick is pushed, -1..1, refreshed by the gamepad poll.
    private double _descInput;

    // Current speed in pixels per second. Kept separate from the input so the text ramps up and
    // glides to a stop instead of starting and stopping with the stick.
    private double _descSpeed;
    private DispatcherTimer? _descTimer;

    // Top speed at full deflection, and how fast the speed itself may change. The second number is
    // what makes it feel like a phone: at 9 the text reaches full speed in about a tenth of a second
    // and takes about as long to stop.
    private const double DescTopSpeed = 950;
    private const double DescResponse = 9;

    /// <summary>Rail geometry, measured on the console - see the scrollbar's comment in the XAML.</summary>
    private const double DescTrackTop = 336;
    private const double DescTrackHeight = 482;

    private void ResetDescriptionScroll()
    {
        _descScroll = 0;
        _descInput = 0;
        _descSpeed = 0;
        _descTimer?.Stop();
        S2Description.RenderTransform = new TranslateTransform(0, 0);

        // Measured against the real width, not Bounds: at this point the block has not been laid out
        // yet on a freshly built page, so Bounds is still zero and the text would look like it fits.
        S2Description.Measure(new Size(616, double.PositiveInfinity));
        _descMax = Math.Max(0, S2Description.DesiredSize.Height - DescViewport);

        UpdateDescriptionThumb();
        UpdateDescriptionBar();
    }

    /// <summary>Shows the rail only on the focused card, and only when there is somewhere to go.</summary>
    private void UpdateDescriptionBar() =>
        S2DescBar.IsVisible = _section == 1 && _card == DescriptionCard && _descMax > 0.5;

    /// <summary>
    /// How hard the right stick is being pushed, -1..1. Fed by the gamepad poll; the motion itself
    /// runs on its own clock, because the poll only fires 20 times a second and text that jumps 20
    /// times a second reads as stuttering however small the steps are.
    /// </summary>
    public void SetDescriptionScrollInput(double input)
    {
        _descInput = Math.Clamp(input, -1, 1);

        if (_descInput == 0 && _descSpeed == 0)
        {
            return;
        }

        if (_descTimer is null)
        {
            _descTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _descTimer.Tick += (_, _) => CrashLog.Guard(StepDescriptionScroll, "descscroll");
        }

        if (!_descTimer.IsEnabled)
        {
            _descTimer.Start();
        }
    }

    private void StepDescriptionScroll()
    {
        const double Dt = 0.016;

        if (_section != 1 || _card != DescriptionCard || _descMax <= 0)
        {
            _descInput = 0;
            _descSpeed = 0;
            _descTimer?.Stop();
            return;
        }

        // Speed chases the stick rather than copying it. Exponential rather than a fixed step so the
        // approach is fast at first and settles, which is what reads as "smooth".
        var objetivo = _descInput * DescTopSpeed;
        _descSpeed += (objetivo - _descSpeed) * Math.Min(1, DescResponse * Dt);

        var antes = _descScroll;
        _descScroll = Math.Clamp(_descScroll + _descSpeed * Dt, 0, _descMax);

        // Hitting either end kills the speed: without this it keeps "pushing" against the stop and
        // takes a moment to respond when the stick comes back the other way.
        if (_descScroll <= 0 || _descScroll >= _descMax)
        {
            _descSpeed = 0;
        }

        if (Math.Abs(_descScroll - antes) > 0.01)
        {
            S2Description.RenderTransform = new TranslateTransform(0, -_descScroll);
            UpdateDescriptionThumb();
        }

        // Nothing to do and nothing being asked for: stop the clock rather than run it forever.
        if (_descInput == 0 && Math.Abs(_descSpeed) < 1)
        {
            _descSpeed = 0;
            _descTimer?.Stop();
        }
    }

    private void UpdateDescriptionThumb()
    {
        if (_descMax <= 0)
        {
            return;
        }

        var total = _descMax + DescViewport;
        var alto = Math.Max(40, DescTrackHeight * DescViewport / total);
        var arriba = DescTrackTop + (DescTrackHeight - alto) * (_descScroll / _descMax);
        S2DescThumb.Height = alto;
        Canvas.SetTop(S2DescThumb, arriba);

        // The R hangs off the bottom of the thumb and rides with it.
        Canvas.SetTop(S2DescHint, arriba + alto);
    }

    // Geometry of a Features row, measured on the console - see the card's comment in the XAML.
    private const double FeatFirstRow = 336;
    private const double FeatRowPitch = 66;
    private const double FeatBarLeft = 32;
    private const double FeatTextLeft = 60;

    /// <summary>
    /// Builds the Features card, or hides it. Rebuilt on every product rather than kept around: the
    /// number of rows changes, and a product with none must not leave the previous one's showing.
    /// </summary>
    private void FillFeatures(IReadOnlyList<string>? features)
    {
        S2FeatList.Children.Clear();

        var any = features is { Count: > 0 };
        S2FeatCard.IsVisible = any;
        S2FeatTitle.IsVisible = any;

        // Drives how far Right can walk. A product with no features has two cards, not three.
        _cardCount = any ? 3 : 2;
        if (!any)
        {
            return;
        }

        for (var i = 0; i < features!.Count; i++)
        {
            var top = FeatFirstRow + FeatRowPitch * i;

            var bar = new Border
            {
                Width = 8,
                Height = 36,
                Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4)),
            };
            Canvas.SetLeft(bar, FeatBarLeft);
            Canvas.SetTop(bar, top);
            S2FeatList.Children.Add(bar);

            var text = new TextBlock
            {
                Text = features[i],
                FontSize = 26,
                Foreground = Brushes.White,
            };
            Canvas.SetLeft(text, FeatTextLeft);
            // +10 puts the cap top on the measured y, not the line box.
            Canvas.SetTop(text, top + 10);
            S2FeatList.Children.Add(text);
        }
    }

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
            return null; // no artwork: the box stays blank and the page still works
        }
    }

    // "ENTERTAINMENT" -> "Entertainment". apps.json holds the genre uppercased because the category
    // page draws it that way inside a pill; here it is running text.
    private static string Capitalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }

    public void Move(GamepadButton button)
    {
        if (_dialogOpen)
        {
            switch (button)
            {
                case GamepadButton.B:
                    CloseDialog();
                    return;
                case GamepadButton.A when _dialogButton == 0:
                    CloseDialog(); // GOT IT. The other two are drawn but do nothing yet.
                    return;
                case GamepadButton.Left when _dialogButton > 0:
                    _dialogButton--;
                    break;
                case GamepadButton.Right when _dialogButton < DialogButtons - 1:
                    _dialogButton++;
                    break;
                default:
                    return;
            }

            UpdateDialogButtons();
            return;
        }

        // Section 2: the two cards take focus left to right, and it is left with Up or B.
        if (_section == 1)
        {
            switch (button)
            {
                case GamepadButton.B:
                case GamepadButton.Up:
                    GoToSection(0);
                    return;
                case GamepadButton.Left when _card > 0:
                    _card--;
                    break;
                case GamepadButton.Right when _card < _cardCount - 1:
                    _card++;
                    break;
                default:
                    return;
            }

            UpdateCard();
            return;
        }

        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.A when _focus == ActionButton:
            {
                // GET both acquires the product and starts its install, and the console confirms
                // that with a dialog. INSTALL and PLAY act with no dialog, so the label decides -
                // read before the action, which is what relabels the button.
                var wasGet = ActionText.Text == "GET";
                ActionInvoked?.Invoke(_art);
                if (wasGet)
                {
                    OpenDialog();
                }

                return;
            }
            case GamepadButton.Down:
                GoToSection(1);
                return;
            case GamepadButton.Left when _focus == WishButton:
                _focus = ActionButton;
                break;
            case GamepadButton.Right when _focus == ActionButton:
                _focus = WishButton;
                break;
            default:
                return;
        }

        UpdateSelection();
    }

    /// <summary>
    /// Moves between the page's sections. The one being left disappears at once (the console does not
    /// animate it out); the one arriving enters offset in the direction of travel and slides in while
    /// fading, and the background crossfades to that section's tone at the same time.
    /// </summary>
    private void GoToSection(int section)
    {
        if (section == _section) return;

        var goingDown = section > _section;
        _section = section;

        Section1.IsVisible = section == 0;
        Section2.IsVisible = section == 1;
        Section2Bg.Opacity = section == 1 ? 1 : 0;

        // The About card is where focus lands on arrival, as on the console. Reset every time, so
        // coming back does not resume on whichever card was left focused.
        _card = AboutCard;
        UpdateCard();

        UpdateDots();

        var entering = section == 1 ? (Control)Section2 : Section1;
        SlideIn(entering, goingDown ? 1 : -1);
    }

    // Same entrance as the library's category change; the numbers are the ones measured there.
    private static void SlideIn(Control target, int direction)
    {
        const double Offset = 190;

        var transitions = target.Transitions;
        target.Transitions = null;
        target.RenderTransform = TransformOperations.Parse(
            $"translateY({(direction * Offset).ToString(CultureInfo.InvariantCulture)}px)");
        target.Opacity = 0;
        target.Transitions = transitions;

        Dispatcher.UIThread.Post(() =>
        {
            target.RenderTransform = TransformOperations.Parse("translateY(0px)");
            target.Opacity = 1;
        }, DispatcherPriority.Render);
    }

    // The unlit dots are NOT a fixed colour: on the near-black first section they measure 79, on the
    // lighter second one 135. Same lit tone in both. Keeping 79 on the light background makes them
    // read as holes rather than as dots.
    private static readonly IBrush DotIdleDark = new SolidColorBrush(Color.FromRgb(0x4F, 0x4F, 0x4F));
    private static readonly IBrush DotIdleLight = new SolidColorBrush(Color.FromRgb(0x87, 0x89, 0x88));

    private void OpenDialog()
    {
        _dialogOpen = true;
        _dialogButton = 0;
        GetDialog.IsVisible = true;
        UpdateDialogButtons();
    }

    private void CloseDialog()
    {
        _dialogOpen = false;
        GetDialog.IsVisible = false;
    }

    private void UpdateDialogButtons()
    {
        var fills = new[] { DlgBtn0Bg, DlgBtn1Bg };
        var labels = new[] { DlgBtn0Text, DlgBtn1Text };
        var rings = new[] { DlgRing0, DlgRing1 };
        var inners = new[] { DlgRing0In, DlgRing1In };

        for (var i = 0; i < fills.Length; i++)
        {
            var on = i == _dialogButton;
            fills[i].Background = on ? DialogFillFocused : DialogFillIdle;
            labels[i].Foreground = on ? LabelFocused : LabelIdle;
            rings[i].Classes.Set("selected", on);
            inners[i].Classes.Set("selected", on);
        }
    }

    // Unlike the product page's buttons, these change FILL as well as label: 55,59,60 focused
    // against 44,49,50 idle, measured on the reference.
    private static readonly IBrush DialogFillFocused = new SolidColorBrush(Color.FromRgb(0x37, 0x3B, 0x3C));
    private static readonly IBrush DialogFillIdle = new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x32));

    private void UpdateCard()
    {
        // The ring never moves. What moves is the row underneath it, one card width per step, so
        // whichever card is selected ends up in the left slot the ring sits on.
        var on = _section == 1;
        S2Ring.Classes.Set("selected", on);
        S2RingInner.Classes.Set("selected", on);

        S2Row.RenderTransform = TransformOperations.Parse(
            $"translateX({(-CardPitch * _card).ToString(CultureInfo.InvariantCulture)}px)");

        // The rail belongs to the focused card, so it comes and goes with the focus.
        UpdateDescriptionBar();
    }

    private void UpdateDots()
    {
        var on = new[] { Dot0On, Dot1On };
        var off = new[] { Dot0Off, Dot1Off };
        var idle = _section == 0 ? DotIdleDark : DotIdleLight;
        for (var i = 0; i < on.Length; i++)
        {
            on[i].Opacity = i == _section ? 1 : 0;
            off[i].Opacity = i == _section ? 0 : 1;
            off[i].Fill = idle;
        }
    }

    // Both buttons keep the same dark fill whatever the focus; what dims is the LABEL. Measured on
    // the console: pure white with focus, #BABCBE without. Without this the unfocused button reads
    // as still selected, since the fill gives no other cue.
    private static readonly IBrush LabelFocused = Brushes.White;
    private static readonly IBrush LabelIdle = new SolidColorBrush(Color.FromRgb(0xBA, 0xBC, 0xBE));

    private void UpdateSelection()
    {
        var onAction = _focus == ActionButton;
        ActionRing.Classes.Set("selected", onAction);
        ActionRingInner.Classes.Set("selected", onAction);
        WishRing.Classes.Set("selected", !onAction);
        WishRingInner.Classes.Set("selected", !onAction);

        ActionText.Foreground = onAction ? LabelFocused : LabelIdle;
        ActionSubText.Foreground = onAction ? LabelFocused : LabelIdle;
        WishIcon.Foreground = onAction ? LabelIdle : LabelFocused;
    }
}
