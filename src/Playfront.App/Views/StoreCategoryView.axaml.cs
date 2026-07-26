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
/// Pagina de CATEGORIA de la Tienda (la que se abre en Apps > "Music apps"): banner grande arriba y
/// rejilla de tarjetas de app debajo.
///
/// TODOS los numeros de esta pantalla (coordenadas, colores, tamanos de fuente) salen del estudio
/// medido sobre la captura de referencia. Antes de mover
/// un valor "porque se ve raro", conviene mirar ahi si esta medido y con que metodo.
///
/// Se monta bajo demanda y se libera al salir (arquitectura de rendimiento del proyecto).
/// </summary>
public partial class StoreCategoryView : UserControl
{
    /// <summary>Se pide volver a la Tienda (B).</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Se pide abrir la FICHA DE PRODUCTO de una app (A sobre una tarjeta). El valor es el nombre del
    /// fichero de arte, que es como se identifica cada app en apps.json. Solo se dispara para las
    /// tarjetas que tienen ficha; sobre las demas A no hace nada.
    /// </summary>
    public event Action<string>? AppRequested;

    // ===== Geometria de la rejilla (medida; ver estudio, seccion 3.3) =====
    private const double GridLeft = 117;
    private const double GridTop = 583;
    private const double CardW = 256;
    private const double CardH = 352;
    private const double ArtH = 256;   // el arte es cuadrado y full-bleed
    private const double FooterH = 96;
    private const double PitchX = 287.3; // con 288 la sexta tarjeta se desviaria 4 px
    private const double PitchY = 384.3;
    private const int Cols = 6;
    private const int Rows = 2;

    // Tinte del pie. El estudio dio #2D3234 al 67%; comparado el render con la captura, salia unos
    // 3-4 niveles oscuro en toda la fila, asi que se aclara el tinte lo justo (mismo alfa).
    private static readonly IBrush FooterFill = new SolidColorBrush(Color.Parse("#AB313639"));
    private static readonly IBrush Placeholder = new SolidColorBrush(Color.Parse("#242A2D"));

    // Ficha de una app, leida de Assets/Icons/Store/Apps/apps.json. Los textos son EXACTAMENTE los de
    // la captura de referencia (la ficha que muestra la Store de XBOX), no los del
    // catalogo de la Store de PC: son distintos -titulo, valoracion, numero de votos y descripcion no
    // coinciden- y lo que se quiere es que salga igual que en la captura.
    // "Tint" es el color con el que se tiñe TODA la pagina al enfocar esa app y "Band" el de las
    // franjas laterales de la caja de arte; los dos salen del propio arte (media del PNG x 0,36 y
    // x 0,26, regla medida).
    // OJO: aqui solo esta Apple Music a proposito. Las fichas se van haciendo DE UNA EN UNA, cada una
    // con su captura; no rellenar las demas por adelantado.
    // Los tres ultimos campos (editor y clasificacion por edad) NO salen en esta pagina: son de la
    // FICHA DE PRODUCTO (StoreAppView), que lee el mismo apps.json. Van aqui para no tener dos
    // ficheros de datos con lo mismo. Opcionales: la app que no tenga captura de su ficha los deja
    // vacios en vez de inventarselos.
    internal sealed record AppInfo(
        string File, string Title, string Genre, string Price, bool Owned,
        double Rating, string Votes, string Description, string Tint, string? Friends = null,
        string? Publisher = null, string? Esrb = null, string? EsrbNotes = null);

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
            return new Dictionary<string, AppInfo>(); // sin fichas: la pagina sigue funcionando
        }
    }

    // Una tarjeta: el arte (fichero dentro de Assets/Icons/Store/Apps, o null si aun no lo tenemos)
    // y la etiqueta del pie. La fila 2 y la 6a de la fila 1 quedan pendientes: la captura las corta
    // o no son identificables, y ese arte se anadira mas adelante.
    private sealed record Card(string? Art, string Label, bool Owned);

    private static readonly Card[] Row1 =
    {
        // En la captura de la Store varias ponen "Owned" (de la cuenta de esa captura). Aqui van
        // TODAS como "Free": sin tick y con el texto pegado al borde, como las otras.
        new("apple-music.png",  "Free",  false),
        new("youtube.png",      "Free",  false),
        new("dolby-access.png", "Free",  false),
        new("spotify-black.png", "Free",  false),
        new("pandora.png",      "Free",  false),
        new(null,               "Free",  false),
    };

    // ===== Foco =====
    // 0 = banner; 1.. = tarjetas por filas (fila 1 = 1..6, fila 2 = 7..12).
    private int _focus;
    private readonly List<Border> _cardRings = new();
    private readonly List<Border> _cardRingsInner = new();
    private readonly List<Border> _cardVeils = new();

    public StoreCategoryView()
    {
        InitializeComponent();

        BannerIcon0.Source = LoadArt("pandora.png");
        BannerIcon1.Source = LoadArt("spotify-black.png");
        BannerIcon2.Source = LoadArt("amazon-music-zoom.png");

        BuildGrid();
        UpdateSelection();
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
            return null; // si falta el fichero, la tarjeta se queda con su caja oscura
        }
    }

    // Construye las 12 tarjetas. Cada una es: arte cuadrado full-bleed + pie translucido con su
    // etiqueta, todo recortado por las esquinas redondeadas del contenedor.
    private void BuildGrid()
    {
        for (var i = 0; i < Cols * Rows; i++)
        {
            var row = i / Cols;
            var col = i % Cols;
            var x = GridLeft + col * PitchX;
            var y = GridTop + row * PitchY;
            var data = row == 0 ? Row1[col] : new Card(null, "Free", false);

            // OJO: la tarjeta NO lleva fondo propio. El pie es TRANSLUCIDO y lo que tiene que
            // transparentarse es el FONDO DE LA PAGINA (por eso en la referencia el pie se vuelve
            // mas verde en las tarjetas de la derecha). Con un fondo opaco en la tarjeta, el pie se
            // mezclaria con el y todas las tarjetas saldrian del mismo color.
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
                // Sin arte todavia (la 6a de la fila 1 y toda la fila 2: la captura las corta o no
                // son identificables; ese arte se anadira mas adelante). Caja oscura SOLO en
                // la zona del arte, no en toda la tarjeta, para no tapar el pie translucido.
                var box = new Rectangle { Width = CardW, Height = ArtH, Fill = Placeholder };
                inner.Children.Add(box);
            }

            // Pie: tinte OSCURO translucido (#2D3234 al 67%), NO un velo blanco -> lo que se ve por
            // detras es el fondo de la pagina, asi que el pie se vuelve mas verde hacia la derecha.
            var footer = new Border
            {
                Width = CardW,
                Height = FooterH,
                Background = FooterFill,
            };
            Canvas.SetTop(footer, ArtH);
            inner.Children.Add(footer);

            // Contenido del pie, colocado por coordenadas absolutas: centrarlo automaticamente lo
            // dejaria ~4,7 px mas abajo de lo medido.
            var textLeft = data.Owned ? 54d : 16d;
            var label = new TextBlock
            {
                Text = data.Label,
                FontFamily = new FontFamily("Segoe UI"), // ver nota de la fuente en el XAML
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

            // Velo del ~8% de negro: en la Store las tarjetas NO enfocadas se ven al 92% de brillo
            // (medido varias veces por separado para estar seguros). Asi no hay que tocar los PNG y el efecto es
            // reversible al enfocar.
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

            // Anillo de foco de TARJETA. Medido en "apple music.png": es mas fino que el del banner
            // (trazo verde ~3,4 y banda negra ~3,4, no 6 y 5), vuela 6-7 px sobre la tarjeta y sus
            // radios van creciendo hacia fuera (7 el arte, 9 la banda, 13 el verde).
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

    // Icono de "Owned": aro fino con un tick dentro, del mismo grosor, blanco. Caja 24x24; el brazo
    // largo sube a 45 grados exactos y su punta roza la cara interior del aro.
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
                // Solo abren ficha las tarjetas que tienen datos en apps.json (de momento, las que
                // aparecen en las capturas de referencia). Sobre el resto, A no hace nada.
                var card = CardAt(_focus - 1).Art;
                if (card is not null && Apps.ContainsKey(card))
                {
                    AppRequested?.Invoke(card);
                }

                return;
            }

            case GamepadButton.Down:
                // Del banner se baja a la primera tarjeta de la fila 1; entre filas se mantiene la
                // columna.
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
                else if (_focus >= 1)
                {
                    _focus = 0; // vuelta al banner
                }

                break;
            case GamepadButton.Left:
                // Izquierda en la primera columna no hace nada (aqui no hay barra lateral).
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
            _cardVeils[i].IsVisible = !on; // la tarjeta enfocada recupera su brillo completo
        }

        // Cambio de estado del bloque de arriba: con el foco en el banner se ve el banner; con el
        // foco en una tarjeta, el banner desaparece del todo (no se atenua: no esta) y en su sitio
        // salen los datos de la app, los avisos de arriba a la derecha y el fondo teñido.
        var art = onBanner ? null : CardAt(_focus - 1).Art;
        var info = art is not null && Apps.TryGetValue(art, out var found) ? found : null;

        BannerBox.IsVisible = onBanner;
        DetailLayer.IsVisible = !onBanner;
        TopHints.IsVisible = !onBanner;

        // El fondo es lo UNICO que no cambia de golpe: se funde (ver SetAmbient).
        SetAmbient(info, art, onBanner);

        if (onBanner)
        {
            return;
        }

        ShowDetail(info, art);
    }

    private static Card CardAt(int index)
    {
        var row = index / Cols;
        var col = index % Cols;
        return row == 0 ? Row1[col] : new Card(null, "Free", false);
    }

    // Rellena el bloque de datos con la ficha de la app enfocada y tiñe el fondo con su color. Si de
    // esa tarjeta no tenemos ficha todavia (las 6 que faltan), se deja el bloque en blanco y el fondo
    // en un gris neutro, en vez de dejar puestos los datos de la anterior.
    private void ShowDetail(AppInfo? info, string? art)
    {
        DetailTitle.Text = info?.Title ?? string.Empty;
        DetailDescription.Text = info?.Description ?? string.Empty;
        GenreText.Text = info?.Genre ?? string.Empty;
        FriendsRow.IsVisible = !string.IsNullOrEmpty(info?.Friends);
        FriendsCount.Text = info?.Friends ?? string.Empty;
        RatingVotes.Text = info?.Votes ?? string.Empty; // tal cual sale en la captura, sin formatear
        BuildStars(info?.Rating ?? 0);

        // Linea de estado: "Owned" con su icono si la app ya es tuya; si no, el precio.
        var owned = info?.Owned == true;
        OwnedIconHost.IsVisible = owned;
        OwnedIconHost.Children.Clear();
        if (owned)
        {
            var icon = BuildCheckIcon(35, 2.6);
            // Medido en las capturas de Spotify y Dolby: el icono y el texto van centrados entre si
            // (mismo centro vertical). El 7,5 sale de bajar 1,5 px el 9 que habia, que dejaba el icono
            // un pelin mas bajo que la referencia.
            Canvas.SetTop(icon, 7.5);
            OwnedIconHost.Children.Add(icon);
            DetailPrice.Text = "Owned";
            Canvas.SetLeft(DetailPrice, 160); // deja sitio al icono (hueco medido de 12)
        }
        else
        {
            DetailPrice.Text = info?.Price ?? string.Empty;
            Canvas.SetLeft(DetailPrice, 114);
        }

        // La linea de estado va debajo de la ULTIMA linea de descripcion, no a una altura fija: con
        // una sola linea sube 49 px respecto a dos (medido comparando las dos fichas).
        var lines = string.IsNullOrEmpty(info?.Description) ? 0 : DescriptionLines(info.Description);
        Canvas.SetTop(StatusRow, 300 + Math.Max(0, lines - 1) * 35 + 38);

        // De las apps cuya ficha aun no se ha hecho no se enseña media pantalla: se ocultan las
        // pastillas y la caja de arte, y el fondo se queda neutro. Se iran haciendo de una en una.
        PillRow.IsVisible = info is not null;
        DetailArtBox.IsVisible = info is not null;

        var bitmap = LoadArt(art);
        DetailArt.Source = bitmap;
        DetailArtBlur.Source = bitmap; // relleno de las franjas: el mismo arte, desenfocado
    }

    // ===== Fondo ambiente con FUNDIDO entre apps =====
    // Dos capas identicas: la de abajo (_ambBase) esta siempre al 100% con el fondo que se ve ahora, y
    // la de arriba (_ambTop) entra de 0 a 100 con el fondo nuevo. Al llegar otro cambio se dan por
    // terminados los papeles (se intercambian) y empieza un fundido nuevo. Se hace asi -y no apagando
    // una mientras se enciende la otra- porque durante ese cruce las dos serian semitransparentes a la
    // vez y asomaria el fondo morado de la pagina por debajo.
    private sealed record AmbientLayers(Canvas Root, Rectangle Fill, Image Art);

    private AmbientLayers? _ambBase, _ambTop;
    private bool _ambientShown;

    private void SetAmbient(AppInfo? info, string? art, bool onBanner)
    {
        _ambBase ??= new AmbientLayers(AmbientA, AmbientFillA, AmbientArtA);
        _ambTop ??= new AmbientLayers(AmbientB, AmbientFillB, AmbientArtB);

        // En el banner no hay fondo de app: se funde a la nada y se deja ver el de la pagina.
        if (onBanner)
        {
            AmbientHost.Opacity = 0;
            _ambientShown = false;
            return;
        }

        (_ambBase, _ambTop) = (_ambTop, _ambBase);
        SetOpacityNow(_ambBase.Root, 1); // lo que hubiera a medias se da por terminado
        SetOpacityNow(_ambTop.Root, 0);
        _ambBase.Root.ZIndex = 0;
        _ambTop.Root.ZIndex = 1;

        FillAmbient(_ambTop, info, art);

        if (_ambientShown)
        {
            _ambTop.Root.Opacity = 1; // fundido de la app anterior a la nueva
        }
        else
        {
            SetOpacityNow(_ambTop.Root, 1); // veniamos del banner: el fundido lo hace el contenedor
        }

        AmbientHost.Opacity = 1;
        _ambientShown = true;
    }

    // Pinta una de las dos capas con el fondo de una app: color plano MEDIDO en su captura (no
    // calculado) + el arte ampliado y desenfocado, centrado en la tarjeta enfocada, que es lo que hace
    // la Store de verdad.
    private void FillAmbient(AmbientLayers layer, AppInfo? info, string? art)
    {
        layer.Fill.Fill = new SolidColorBrush(Color.Parse(info?.Tint ?? "#2A2A2A"));

        // La mancha: el arte YA OSCURECIDO (fichero *-ambient.png). Medido en la captura: su centro en
        // X coincide con el centro del arte de esa tarjeta. No es una mancha pequeña: cubre TODA la
        // pantalla. Con arte uniforme (YouTube, blanco) el fondo sale plano y solo asoma el logo cerca
        // de su tarjeta; con arte que varia (Dolby, degradado teal-azul-morado) el fondo entero varia
        // igual que el arte, que es justo lo que se mide en esas capturas.
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

    // Cambia la opacidad SIN fundido (anulando la transicion un instante), para colocar el estado de
    // partida de una capa antes de animarla.
    private static void SetOpacityNow(Control control, double value)
    {
        var transitions = control.Transitions;
        control.Transitions = null;
        control.Opacity = value;
        control.Transitions = transitions;
    }

    // Cuantas lineas ocupa la descripcion con el ancho del bloque. Se mide de verdad (no se estima
    // por numero de caracteres) para que la linea de estado caiga donde toca.
    private int DescriptionLines(string text)
    {
        DetailDescription.Text = text;
        DetailDescription.Measure(new Size(DetailDescription.Width, double.PositiveInfinity));
        return Math.Max(1, (int)Math.Round(DetailDescription.DesiredSize.Height / 35));
    }

    // Icono de circulo con tick, el mismo de los pies de las tarjetas pero mas grande.
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

    // Las 5 estrellas de la valoracion: rellenas las que marque la nota, de contorno el resto.
    // Medido: caja 23x22, paso 24, la rellena blanca solida y la vacia de trazo ~2.
    private void BuildStars(double rating)
    {
        StarHost.Children.Clear();
        var filled = (int)Math.Round(rating);
        for (var i = 0; i < 5; i++)
        {
            var star = new Path
            {
                Data = Geometry.Parse("M12,2 L15.1,8.3 L22,9.3 L17,14.1 L18.2,21 L12,17.8 L5.8,21 L7,14.1 L2,9.3 L8.9,8.3 Z"),
                Stretch = Stretch.Uniform,
                Width = 23,
                Height = 22,
            };
            if (i < filled)
            {
                star.Fill = Brushes.White;
            }
            else
            {
                star.Stroke = Brushes.White;
                star.StrokeThickness = 1.6;
            }

            Canvas.SetLeft(star, i * 24);
            Canvas.SetTop(star, 10);
            StarHost.Children.Add(star);
        }
    }
}
