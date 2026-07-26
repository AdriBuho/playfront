using System;
using System.Globalization;
using System.IO;
using Playfront.App.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;

namespace Playfront.App.Views;

// Vista "My games & apps" (biblioteca). Recreada 1:1 de la captura de referencia; navegable con el mando.
//
// Dos estados de foco:
//   - MENU: foco en la columna de categorias. La categoria enfocada lleva el resalte verde SOLIDO
//     (LMenuHighlight). Al moverse arriba/abajo, el CONTENIDO de la derecha cambia SOLO a la categoria
//     resaltada (titulo + panel): Games -> rejilla de juegos, Manage -> tarjetas de administrar; las
//     otras 4 (Apps/Groups/Game history/Full library) aun no tienen diseño, muestran solo su titulo.
//   - CONTENT: foco dentro del contenido. La categoria activa pasa al indicador de "abierta" (franja
//     gris + barrita verde, LCategoryIndicator) y el elemento enfocado (juego o tarjeta) lleva el anillo.
//
// A/Derecha sobre una categoria CON contenido (Games o Manage) entra en el contenido; B/Izquierda(col 0)
// vuelve al menu; B en el menu sale a la home.
public partial class LibraryView : UserControl
{
    // La ventana escucha esto para volver a la home.
    public event Action? ExitRequested;

    private enum FocusArea { Menu, Content }

    private FocusArea _focus = FocusArea.Menu;
    private int _menuIndex;      // 0=Games,1=Apps,2=Groups,3=Game history,4=Full library,5=Manage
    private int _contentIndex;   // dentro de la categoria activa: juego (Games 0..9) o tarjeta (Manage 0..8)

    private const int GamesCat = 0;
    private const int ManageCat = 5;

    // Nombre mostrado (titulo) de cada categoria. Texto en ingles (norma del proyecto).
    private static readonly string[] CategoryNames = { "Games", "Apps", "Groups", "Game history", "Full library", "Manage" };

    // Canvas.Top de cada categoria del menu (el resalte/indicador se desplaza a estas alturas). Los
    // primeros cinco van seguidos; "Manage" cae mas abajo por el divisor.
    private static readonly double[] MenuRowTops = { 181, 255, 328, 402, 475, 592 };

    // Rejilla de juegos (Games): 6 casillas arriba, 4 abajo.
    private static readonly int[] GridRowLengths = { 6, 4 };

    // Rejilla de tarjetas de Manage por filas (2 columnas; la ultima fila solo tiene la izquierda).
    private static readonly int[][] ManageGrid =
    {
        new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 }, new[] { 8 },
    };

    private Border[] _rings = null!;        // anillos de los juegos (Games)
    private Border[] _labels = null!;       // etiquetas con el nombre del juego (Games)
    private Border[] _manageRings = null!;  // anillos de las tarjetas (Manage)

    public LibraryView()
    {
        InitializeComponent();

        // Nombre real de Playfront (igual que Home/Settings), no el de la captura de referencia.
        LibraryUserName.Text = Environment.UserName;

        _rings = new[] { LRing0, LRing1, LRing2, LRing3, LRing4, LRing5, LRing6, LRing7, LRing8, LRing9 };
        _labels = new[] { LLabel0, LLabel1, LLabel2, LLabel3, LLabel4, LLabel5, LLabel6, LLabel7, LLabel8, LLabel9 };
        _manageRings = new[] { MRing0, MRing1, MRing2, MRing3, MRing4, MRing5, MRing6, MRing7, MRing8 };

        UpdateStorage();
        UpdateVisuals();
    }

    // Cuantos elementos navegables tiene la categoria activa (0 si no tiene contenido).
    private int ContentCount => _menuIndex == GamesCat ? _rings.Length
        : _menuIndex == ManageCat ? _manageRings.Length : 0;

    // ===== Medidor de almacenamiento (abajo-izquierda) =====
    // Geometria del anillo (coincide con el XAML): centro y radio del trazo.
    private const double RingCx = 330, RingCy = 993, RingR = 42.5;

    // Rellena "available" (GB libres), el % del centro y el arco verde con el espacio LIBRE REAL del
    // disco del sistema (donde esta Windows/los juegos). El arco = % libre; el resto (hueco) lo deja
    // ver el track. Si por lo que sea no se puede leer el disco, se deja lo que haya en el XAML.
    private void UpdateStorage()
    {
        try
        {
            var sysRoot = Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";
            var drive = new DriveInfo(sysRoot);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return;
            }

            double free = drive.AvailableFreeSpace;
            double total = drive.TotalSize;
            var percentFree = free / total * 100.0;
            // GB "de Windows" = base 1024 (lo que ve el usuario en el Explorador), no base 10.
            var freeGiB = free / (1024.0 * 1024.0 * 1024.0);

            LStorageSize.Text = FormatSize(freeGiB);
            LStoragePercent.Text = percentFree.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            LStorageArc.Data = BuildRingArc(percentFree);
        }
        catch (Exception)
        {
            // Best-effort: si falla la lectura del disco, se queda el placeholder del XAML.
        }
    }

    private static string FormatSize(double giB)
    {
        if (giB >= 1024)
        {
            return (giB / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " TB";
        }

        // >=100 GB sin decimales (p.ej. "680 GB"); por debajo, un decimal (p.ej. "59.8 GB").
        return giB.ToString(giB >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB";
    }

    // Construye el arco del anillo para un porcentaje (0..100): el 0% esta ARRIBA (12 en punto, como en
    // la captura) y el trazo se rellena en sentido HORARIO ese porcentaje; el hueco restante queda a la
    // izquierda de arriba.
    private static Geometry BuildRingArc(double percent)
    {
        percent = Math.Clamp(percent, 0.5, 99.9); // evita arcos degenerados (0% o 100% exactos)
        var sweep = percent / 100.0 * 360.0;
        var (x1, y1) = ClockPoint(0);      // inicio: justo arriba (0%)
        var (x2, y2) = ClockPoint(sweep);  // fin: tras avanzar 'sweep' en sentido horario
        var large = sweep > 180.0 ? 1 : 0;
        var inv = CultureInfo.InvariantCulture;
        var data = $"M{x1.ToString("0.##", inv)},{y1.ToString("0.##", inv)} " +
                   $"A{RingR.ToString(inv)},{RingR.ToString(inv)} 0 {large},1 " +
                   $"{x2.ToString("0.##", inv)},{y2.ToString("0.##", inv)}";
        return Geometry.Parse(data);
    }

    // Punto del anillo a un angulo de "reloj": 0=arriba, sentido horario.
    private static (double x, double y) ClockPoint(double aDeg)
    {
        var rad = aDeg * Math.PI / 180.0;
        return (RingCx + RingR * Math.Sin(rad), RingCy - RingR * Math.Cos(rad));
    }

    // Enrutado del mando desde MainWindow.Move.
    public void Move(GamepadButton button)
    {
        if (_focus == FocusArea.Menu)
        {
            MoveMenu(button);
        }
        else
        {
            MoveContent(button);
        }
    }

    private void MoveMenu(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.Up when _menuIndex > 0:
                _menuIndex--;
                break;
            case GamepadButton.Down when _menuIndex < MenuRowTops.Length - 1:
                _menuIndex++;
                break;
            // Entrar en el contenido de la categoria resaltada, si tiene (Games o Manage).
            case GamepadButton.A when ContentCount > 0:
            case GamepadButton.Right when ContentCount > 0:
                EnterContent();
                return;
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            default:
                return;
        }

        UpdateVisuals();
    }

    // Dentro del contenido: enruta segun la categoria activa.
    private void MoveContent(GamepadButton button)
    {
        if (_menuIndex == GamesCat)
        {
            MoveGamesGrid(button);
        }
        else if (_menuIndex == ManageCat)
        {
            MoveManageGrid(button);
        }
    }

    // Navegacion de la rejilla de JUEGOS (6 arriba + 4 abajo).
    private void MoveGamesGrid(GamepadButton button)
    {
        var row = _contentIndex < GridRowLengths[0] ? 0 : 1;
        var col = row == 0 ? _contentIndex : _contentIndex - GridRowLengths[0];

        switch (button)
        {
            case GamepadButton.Left when col > 0:
                _contentIndex--;
                break;
            // Izquierda desde la primera columna: vuelve al menu (como en Xbox).
            case GamepadButton.Left:
            case GamepadButton.B:
                ExitContent();
                return;
            case GamepadButton.Right when col < GridRowLengths[row] - 1:
                _contentIndex++;
                break;
            case GamepadButton.Down when row == 0:
                _contentIndex = GridRowLengths[0] + Math.Min(col, GridRowLengths[1] - 1);
                break;
            case GamepadButton.Up when row == 1:
                _contentIndex = col;
                break;
            default:
                return;
        }

        UpdateVisuals();
    }

    // Navegacion de las tarjetas de MANAGE (2 columnas; la ultima fila solo tiene la izquierda).
    private void MoveManageGrid(GamepadButton button)
    {
        var (row, col) = ManagePos(_contentIndex);

        switch (button)
        {
            case GamepadButton.Left when col > 0:
                _contentIndex = ManageGrid[row][col - 1];
                break;
            case GamepadButton.Left:
            case GamepadButton.B:
                ExitContent();
                return;
            case GamepadButton.Right when col < ManageGrid[row].Length - 1:
                _contentIndex = ManageGrid[row][col + 1];
                break;
            case GamepadButton.Up when row > 0:
                _contentIndex = ManageGrid[row - 1][Math.Min(col, ManageGrid[row - 1].Length - 1)];
                break;
            case GamepadButton.Down when row < ManageGrid.Length - 1:
                _contentIndex = ManageGrid[row + 1][Math.Min(col, ManageGrid[row + 1].Length - 1)];
                break;
            default:
                return;
        }

        UpdateVisuals();
    }

    // (fila, columna) de una tarjeta de Manage a partir de su indice.
    private static (int row, int col) ManagePos(int card)
    {
        for (var r = 0; r < ManageGrid.Length; r++)
        {
            for (var c = 0; c < ManageGrid[r].Length; c++)
            {
                if (ManageGrid[r][c] == card)
                {
                    return (r, c);
                }
            }
        }

        return (0, 0);
    }

    // Entrar en el contenido de la categoria activa (primer elemento seleccionado).
    private void EnterContent()
    {
        _focus = FocusArea.Content;
        _contentIndex = 0;
        UpdateVisuals();
    }

    // Volver al menu (la categoria activa sigue resaltada).
    private void ExitContent()
    {
        _focus = FocusArea.Menu;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var inContent = _focus == FocusArea.Content;

        // Contenido de la derecha: sigue a la categoria activa (Games / Manage / vacio), y el titulo.
        LContentTitle.Text = CategoryNames[_menuIndex];
        LGamesContent.IsVisible = _menuIndex == GamesCat;
        LManageContent.IsVisible = _menuIndex == ManageCat;

        // Resalte verde solido (foco en menu) vs indicador de "categoria abierta" (foco en contenido).
        // Ambos se colocan en la fila de la categoria activa.
        var shift = TransformOperations.Parse($"translateY({MenuRowTops[_menuIndex] - MenuRowTops[0]}px)");
        LMenuHighlight.IsVisible = !inContent;
        LMenuHighlight.RenderTransform = shift;
        LCategoryIndicator.IsVisible = inContent;
        LCategoryIndicator.RenderTransform = shift;

        // Anillos + etiquetas de JUEGO: solo el elemento enfocado, y solo con foco de contenido en Games.
        var gamesFocus = inContent && _menuIndex == GamesCat;
        for (var i = 0; i < _rings.Length; i++)
        {
            var on = gamesFocus && i == _contentIndex;
            SetClass(_rings[i], "selected", on);
            _labels[i].IsVisible = on;
        }

        // Anillos de las TARJETAS de Manage: solo el enfocado, y solo con foco de contenido en Manage.
        var manageFocus = inContent && _menuIndex == ManageCat;
        for (var i = 0; i < _manageRings.Length; i++)
        {
            SetClass(_manageRings[i], "selected", manageFocus && i == _contentIndex);
        }

        // Pistas de abajo-derecha: las acciones del juego (Manage game / More options) solo con un juego
        // enfocado; en cualquier otro estado, "Search".
        LGridHints.IsVisible = gamesFocus;
        LSearchHint.IsVisible = !gamesFocus;
    }

    private static void SetClass(StyledElement element, string className, bool on)
    {
        if (on)
        {
            if (!element.Classes.Contains(className))
            {
                element.Classes.Add(className);
            }
        }
        else
        {
            element.Classes.Remove(className);
        }
    }
}
