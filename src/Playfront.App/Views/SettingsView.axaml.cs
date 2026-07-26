using System;
using System.Globalization;
using Playfront.App.Input;
using Avalonia.Controls;
using Avalonia.Media.Transformation;

namespace Playfront.App.Views;

/// <summary>
/// Pantalla de Ajustes (1:1 con Xbox). Vive en su propia vista para cargarse BAJO DEMANDA:
/// MainWindow la crea al entrar en Ajustes y la libera al salir (no se queda residente).
/// Arquitectura de rendimiento: nada se construye antes de que el usuario lo abra.
///
/// Contiene su propia navegacion (lista de categorias + una rejilla de tarjetas por categoria).
/// Lo que SALE de esta pantalla se avisa por eventos, para que la vista no dependa de MainWindow:
/// <see cref="PersonalizationRequested"/> (abrir la pantalla de Personalization) y
/// <see cref="ExitRequested"/> (cerrar Ajustes y volver a la home).
/// </summary>
public partial class SettingsView : UserControl
{
    // Canvas.Top de cada SNavX en el XAML (indice = categoria) - se usa para desplazar el resalte
    // por translateY relativo a NavTops[1] (la posicion base de "General").
    private static readonly double[] NavTops = { 153, 216, 280, 344, 408, 472, 536 };

    private static readonly string[] CategoryNames =
    {
        "Recommendations", "General", "Account", "System", "Devices & connections", "Preferences", "Accessibility",
    };

    private const int GeneralCategory = 1;
    private const int SystemCategory = 3;

    private readonly Border[] _navItems;

    // Una rejilla por categoria, indexada igual que CategoryNames; null = categoria sin contenido
    // todavia (se queda como hueco navegable en la lista, igual que en Xbox mientras se construye).
    // Son arrays IRREGULARES a proposito: cada fila declara sus propias columnas, que es lo que
    // permite que la ultima fila de System tenga una sola tarjeta ("Time") sin ningun caso especial
    // en la navegacion - basta con leer la longitud de la fila en la que estas.
    private readonly Border[][]?[] _grids;
    private readonly Border[][]?[] _rings;

    private int _category = GeneralCategory;
    private bool _inGrid;
    private int _gridRow;
    private int _gridCol;

    /// <summary>Se pide abrir la pantalla de Personalization (tarjeta "Personalization" de General).</summary>
    public event Action? PersonalizationRequested;

    /// <summary>Se pide abrir la pantalla "System Updates" (tarjeta "Updates" de System).</summary>
    public event Action? UpdatesRequested;

    /// <summary>Se pide cerrar Ajustes y volver a la home (B en la lista de categorias).</summary>
    public event Action? ExitRequested;

    public SettingsView()
    {
        InitializeComponent();

        _navItems = new[] { SNav0, SNav1, SNav2, SNav3, SNav4, SNav5, SNav6 };

        _grids = new Border[][]?[CategoryNames.Length];
        _rings = new Border[][]?[CategoryNames.Length];

        _grids[GeneralCategory] = new[]
        {
            new[] { SCard0, SCard1 },
            new[] { SCard2, SCard3 },
            new[] { SCard4, SCard5 },
        };
        _rings[GeneralCategory] = new[]
        {
            new[] { SRing0, SRing1 },
            new[] { SRing2, SRing3 },
            new[] { SRing4, SRing5 },
        };

        _grids[SystemCategory] = new[]
        {
            new[] { SysCard0, SysCard1 },
            new[] { SysCard2, SysCard3 },
            new[] { SysCard4, SysCard5 },
            new[] { SysCard6 },
        };
        _rings[SystemCategory] = new[]
        {
            new[] { SysRing0, SysRing1 },
            new[] { SysRing2, SysRing3 },
            new[] { SysRing4, SysRing5 },
            new[] { SysRing6 },
        };

        SettingsUserNameText.Text = global::System.Environment.UserName;
        UpdateSelection();
    }

    private Border[][]? CurrentGrid => _grids[_category];

    public void Move(GamepadButton button)
    {
        var grid = CurrentGrid;

        switch (button)
        {
            // "Personalization" es la tarjeta de la fila 0, columna 1 de la rejilla de General
            // (ver SCard1 en el XAML) - la unica que por ahora abre una pantalla propia. La
            // comprobacion de categoria es imprescindible: sin ella, esa misma posicion en
            // System ("Storage devices") abriria Personalization.
            case GamepadButton.A when _inGrid && _category == GeneralCategory && _gridRow == 0 && _gridCol == 1:
                PersonalizationRequested?.Invoke();
                return;

            // "Updates" es la tarjeta de la fila 1, columna 0 de la rejilla de System (SysCard2).
            case GamepadButton.A when _inGrid && _category == SystemCategory && _gridRow == 1 && _gridCol == 0:
                UpdatesRequested?.Invoke();
                return;

            case GamepadButton.B when _inGrid:
                _inGrid = false;
                break;
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;

            // Se entra en la rejilla con A o con Derecha (las tarjetas estan a la derecha de la
            // lista); Izquierda en la primera columna vuelve a la lista.
            case GamepadButton.A when !_inGrid && grid != null:
            case GamepadButton.Right when !_inGrid && grid != null:
                _inGrid = true;
                _gridRow = 0;
                _gridCol = 0;
                break;

            case GamepadButton.Up when _inGrid && _gridRow > 0:
                _gridRow--;
                ClampColumn();
                break;
            case GamepadButton.Down when _inGrid && grid != null && _gridRow < grid.Length - 1:
                _gridRow++;
                ClampColumn();
                break;
            case GamepadButton.Left when _inGrid && _gridCol > 0:
                _gridCol--;
                break;
            case GamepadButton.Left when _inGrid:
                _inGrid = false;
                break;
            case GamepadButton.Right when _inGrid && grid != null && _gridCol < grid[_gridRow].Length - 1:
                _gridCol++;
                break;

            case GamepadButton.Up when !_inGrid && _category > 0:
                _category--;
                break;
            case GamepadButton.Down when !_inGrid && _category < _navItems.Length - 1:
                _category++;
                break;

            default:
                return;
        }

        UpdateSelection();
    }

    // Al cambiar de fila, la nueva puede tener menos columnas que la anterior (en System la ultima
    // fila tiene una sola tarjeta). Sin esto, bajar desde "Backup & transfer" dejaria la seleccion
    // en una columna que no existe: no se veria ninguna tarjeta seleccionada.
    private void ClampColumn()
    {
        var grid = CurrentGrid;
        if (grid == null) return;

        var lastColumn = grid[_gridRow].Length - 1;
        if (_gridCol > lastColumn) _gridCol = lastColumn;
    }

    private void UpdateSelection()
    {
        // Dos rectangulos compartidos que se desplazan via RenderTransform hasta la fila de la
        // categoria (mismo offset, misma Transition animando el movimiento): SettingsNavHighlight
        // (bloque verde solido, mientras se navega la lista) y SettingsCategoryIndicator (franja
        // gris + barrita verde a la derecha, mientras el foco esta dentro de la rejilla).
        var offsetY = NavTops[_category] - NavTops[GeneralCategory];
        var offsetTransform = TransformOperations.Parse($"translateY({offsetY.ToString(CultureInfo.InvariantCulture)}px)");

        SettingsNavHighlight.IsVisible = !_inGrid;
        SettingsNavHighlight.RenderTransform = offsetTransform;

        SettingsCategoryIndicator.IsVisible = _inGrid;
        SettingsCategoryIndicator.RenderTransform = offsetTransform;

        SettingsCategoryTitle.Text = CategoryNames[_category];

        // Se recorren TODAS las rejillas, no solo la actual: es lo que apaga la de la categoria
        // que se acaba de dejar. Si solo se tocara la actual, las tarjetas de General seguirian
        // dibujadas por debajo de las de System.
        for (var category = 0; category < _grids.Length; category++)
        {
            var grid = _grids[category];
            var rings = _rings[category];
            if (grid == null || rings == null) continue;

            var visible = category == _category;
            for (var r = 0; r < grid.Length; r++)
            {
                for (var c = 0; c < grid[r].Length; c++)
                {
                    var isSelected = visible && _inGrid && r == _gridRow && c == _gridCol;
                    grid[r][c].IsVisible = visible;
                    grid[r][c].Classes.Set("selected", isSelected);
                    // El anillo se oculta con su tarjeta: si no, al cambiar a una categoria sin
                    // rejilla seguiria dibujandose un rectangulo verde flotando sobre el vacio.
                    rings[r][c].IsVisible = visible;
                    rings[r][c].Classes.Set("selected", isSelected);
                }
            }
        }
    }
}
