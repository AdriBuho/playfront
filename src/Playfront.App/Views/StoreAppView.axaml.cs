using System;
using Playfront.App.Input;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

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

    public StoreAppView(string art)
    {
        InitializeComponent();

        _art = art;

        // Artwork is shown at 701 px here, far larger than on the category card. The normal file is
        // 300 px and looked blocky scaled up, so a "-large" variant (514 px) is preferred when one
        // exists. Otherwise the normal one.
        Art.Source = LoadArt(art?.Replace(".png", "-large.png")) ?? LoadArt(art);

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

        // Primary button. The second line is the ownership state: the Xbox capture said "You own this"
        // because that account had the app. Here nothing is owned, so the price shows instead.
        ActionText.Text = "INSTALL";
        ActionSubText.Text = info?.Owned == true ? "You own this" : info?.Price ?? string.Empty;

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

        UpdateSelection();
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
        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.A when _focus == ActionButton:
                ActionInvoked?.Invoke(_art);
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

    private void UpdateSelection()
    {
        var onAction = _focus == ActionButton;
        ActionRing.Classes.Set("selected", onAction);
        ActionRingInner.Classes.Set("selected", onAction);
        WishRing.Classes.Set("selected", !onAction);
        WishRingInner.Classes.Set("selected", !onAction);
    }
}
