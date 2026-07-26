using System;
using Playfront.App.Input;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Playfront.App.Views;

/// <summary>
/// FICHA DE PRODUCTO de la Tienda: la pagina que se abre al pulsar A sobre una tarjeta de la pagina
/// de categoria. Arte grande a la derecha, datos de la app a la izquierda, boton de instalar y
/// bloque de clasificacion por edad abajo a la derecha.
///
/// Todos los numeros de la pantalla estan medidos sobre la captura "youtube.png" del usuario; el
/// detalle esta en el XAML.
///
/// Se monta bajo demanda y se libera al salir (arquitectura de rendimiento del proyecto).
/// </summary>
public partial class StoreAppView : UserControl
{
    /// <summary>Se pide volver a la pagina de categoria (B).</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Se pulsa el boton principal (A sobre INSTALL/PLAY). MainWindow decide que hacer segun la app: para
    /// YouTube, instalarla (si no lo estaba) y lanzarla. El valor es el fichero de arte que identifica la
    /// app en apps.json.
    /// </summary>
    public event Action<string>? ActionInvoked;

    private readonly string _art;

    // Solo hay dos elementos navegables: el boton principal y el de la lista de deseos.
    private const int ActionButton = 0;
    private const int WishButton = 1;

    private int _focus = ActionButton;

    public StoreAppView(string art)
    {
        InitializeComponent();

        _art = art;

        // El arte se ve aqui a 701 px, mucho mas grande que en la tarjeta de la pagina de categoria.
        // El fichero normal es de 300 px y ampliado se veia pixelado, asi que si existe una version
        // "-large" (recortada de la captura de esta pagina, 514 px) se usa esa. Si no, la normal.
        Art.Source = LoadArt(art?.Replace(".png", "-large.png")) ?? LoadArt(art);

        // Los datos salen de apps.json (el mismo fichero que usa la pagina de categoria).
        StoreCategoryView.Apps.TryGetValue(art, out var info);

        TitleText.Text = info?.Title ?? string.Empty;

        // Linea de editor: "<editor> ▪ <genero>". El genero va en la capital que trae apps.json
        // (ahi esta en mayusculas porque la pagina de categoria lo pinta como pastilla), pero en la
        // ficha se lee como palabra normal, asi que se pasa a "Entertainment".
        var genre = Capitalize(info?.Genre);
        var publisher = info?.Publisher;
        PublisherText.Text = string.IsNullOrEmpty(publisher)
            ? genre
            : string.IsNullOrEmpty(genre) ? publisher : $"{publisher}  ▪  {genre}";

        DescriptionText.Text = info?.Description ?? string.Empty;

        // Boton principal. La segunda linea es el ESTADO de compra: en la captura de Xbox ponia "You
        // own this" porque esa cuenta tenia la app; aqui las apps no son de nadie (el usuario pidio
        // que todas las que ponian "Owned" pongan "Free"), asi que sale el precio.
        ActionText.Text = "INSTALL";
        ActionSubText.Text = info?.Owned == true ? "You own this" : info?.Price ?? string.Empty;

        // Clasificacion por edad. Si la app no tiene captura de su ficha, el bloque entero no se
        // pinta (mejor vacio que inventado).
        var esrb = info?.Esrb;
        var hasRating = !string.IsNullOrEmpty(esrb);
        RatingLabel.Text = esrb ?? string.Empty;
        RatingNotes.Text = info?.EsrbNotes ?? string.Empty;
        // El sello es una imagen (el oficial, recortado de la captura), no un dibujo: un fichero por
        // clasificacion. De momento solo existe el de TEEN, que es el unico que hemos visto.
        RatingSeal.Source = hasRating ? LoadIcon($"esrb-{esrb!.ToLowerInvariant()}.png") : null;
        RatingSeal.IsVisible = RatingSeal.Source is not null;
        RatingRule.IsVisible = hasRating;

        UpdateSelection();
    }

    // Iconos sueltos de la Tienda (el sello de edad, de momento).
    private static Bitmap? LoadIcon(string file)
    {
        try
        {
            return new Bitmap(AssetLoader.Open(new Uri($"avares://Playfront.App/Assets/Icons/Store/{file}")));
        }
        catch
        {
            return null; // no tenemos ese sello: el bloque se queda sin el en vez de inventarlo
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
            return null; // sin arte: la caja se queda blanca, la pagina sigue funcionando
        }
    }

    // "ENTERTAINMENT" -> "Entertainment". En apps.json el genero esta en mayusculas porque la pagina
    // de categoria lo pinta asi dentro de una pastilla; aqui es texto corrido.
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
