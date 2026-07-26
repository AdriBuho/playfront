using System;
using System.IO;
using Avalonia;
using Avalonia.Media;

namespace Playfront.App;

// Gestiona el color de acento del tema (el color de las selecciones: bordes, anillos, halos,
// resaltes, circulo de "My color"). Antes el acento era un StaticResource fijo (#439941) con los
// halos escritos literales en el XAML; ahora todo se deriva de UN color base y se publica como
// recursos de la aplicacion que el XAML consume con DynamicResource, para poder cambiarlo en
// caliente desde el selector de color. La eleccion se guarda en disco y se recarga al arrancar.
public static class AccentTheme
{
    // Acento por defecto: "Bright green" (#5AA029), el verde de la rejilla del selector (r1c1). Se
    // eligio uno de los 14 a proposito para que, al entrar al selector por primera vez, el color de
    // sistema ya sea uno de la rejilla y salga su marca de "aplicado". Es el mas cercano al verde
    // aprobado antes (#439941). Si no hay nada guardado, este.
    public const string DefaultHex = "#5AA029";

    // Los 14 colores del selector (ColorPickerScreen) con un nombre para mostrar en "My color".
    // Mismo orden que la rejilla: fila 1 y luego fila 2.
    // El unico nombre confirmado por una captura es "Bright green" (r1c1); el resto son
    // descriptivos razonables (si aparecen los nombres reales de Xbox se cambian aqui).
    // NOTAS:
    // - "Navy" (#25458B, el azul mas oscuro) se sustituyo por BLANCO a proposito, para
    //   poder tener la seleccion en blanco (como la Store de Xbox) eligiendolo como color de tema.
    // - Orden: de MAS CLARO a MAS OSCURO (por luminancia percibida 0.299R+0.587G+0.114B), empezando
    //   por el blanco arriba-izquierda. Fila 1 (indices 0-6) y luego fila 2 (7-13). ColorSwatchHexes
    //   de MainWindow lleva EXACTAMENTE el mismo orden (mismo indice = mismo color).
    public static readonly (string Hex, string Name)[] Palette =
    {
        ("#FFFFFF", "White"),     ("#DB5985", "Pink"),  ("#5AA029", "Bright green"), ("#D84F1F", "Orange"),
        ("#A64AB3", "Orchid"),    ("#207EBB", "Blue"),  ("#7552A1", "Purple"),
        ("#23807F", "Dark teal"), ("#2073C7", "Azure"), ("#217F72", "Teal"),         ("#D01F2F", "Red"),
        ("#B21F75", "Magenta"),   ("#207A1F", "Forest"),("#991F30", "Crimson"),
    };

    private static string SettingsPath => AppData.File("accent.txt");

    // Nombre a mostrar para un color de acento (el valor bajo "My color").
    public static string NameFor(string hex)
    {
        foreach (var (h, n) in Palette)
        {
            if (string.Equals(h, hex, StringComparison.OrdinalIgnoreCase))
            {
                return n;
            }
        }

        return "Custom";
    }

    public static string LoadSavedHex()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var hex = File.ReadAllText(SettingsPath).Trim();
                if (!string.IsNullOrEmpty(hex))
                {
                    return hex;
                }
            }
        }
        catch
        {
            // Si el archivo esta corrupto o no se puede leer, se usa el acento por defecto.
        }

        return DefaultHex;
    }

    public static void Save(string hex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, hex);
        }
        catch
        {
            // Guardar es "best effort": si falla, el tema sigue aplicado en esta sesion, solo no
            // persiste al reiniciar.
        }
    }

    // Publica en los recursos de la app todos los colores/pinceles/sombras derivados del acento.
    // Los halos son el MISMO tono del acento pero mas oscuros y saturados (glow1/2/3, de mas claro
    // a mas oscuro) - la misma relacion que el verde (#439941 -> #007800/#006400/#004B00). Los
    // factores de brillo (0.783/0.65/0.483) reproducen esos tres verdes a partir del acento base,
    // asi que el tema verde por defecto queda visualmente identico al que estaba escrito a mano.
    public static void Apply(Application app, Color accent)
    {
        var glow1 = Derive(accent, 0.783);
        var glow2 = Derive(accent, 0.65);
        var glow3 = Derive(accent, 0.483);

        var res = app.Resources;
        res["AccentColor"] = accent;
        res["AccentBrush"] = new SolidColorBrush(accent);
        res["AccentFadedBrush"] = new SolidColorBrush(Color.FromArgb(0, accent.R, accent.G, accent.B));
        // Version OSCURA del acento (mismo tono), para "tracks" tenues como el hueco del anillo de
        // almacenamiento de la biblioteca (en Xbox ese hueco es un verde oscuro, no gris). Sigue el tema.
        res["AccentTrackBrush"] = new SolidColorBrush(Derive(accent, 0.42));

        // Sombras (halos) reconstruidas con el tono del acento. Los desenfoques/spreads/alpha son
        // los mismos que tenia cada halo escrito a mano; solo cambia el color RGB.
        res["HomeRingShadow"] = BoxShadows.Parse(
            $"0 0 3 1 {Hex(0xC0, accent)}, 0 0 5 3 {Hex(0xF2, glow1)}, 0 0 13 3 {Hex(0xA6, glow2)}, " +
            $"0 0 24 2 {Hex(0x59, glow3)}, inset 0 0 6 4 {Hex(0xF2, glow1)}, inset 0 0 12 3 {Hex(0x99, glow2)}");
        res["SettingsRingShadow"] = BoxShadows.Parse(
            $"0 0 20 0 {Hex(0xFF, glow1)}, 0 0 20 0 {Hex(0xFF, glow1)}, " +
            $"inset 0 0 20 0 {Hex(0xFF, glow1)}, inset 0 0 20 0 {Hex(0xFF, glow1)}");
        res["NavCircleShadow"] = BoxShadows.Parse($"0 0 20 3 {Hex(0x80, accent)}");
        res["SettingsHighlightShadow"] = BoxShadows.Parse($"0 0 20 2 {Hex(0x80, accent)}");
    }

    private static string Hex(byte alpha, Color c) => $"#{alpha:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    // Glow = mismo tono, saturacion al maximo, valor (brillo) reducido por el factor. EXCEPCION: para
    // colores casi sin saturacion (blanco/gris), NO se fuerza la saturacion -eso daria un halo rojo,
    // porque su tono por defecto es 0 (rojo)-: se mantiene el gris y solo se baja el brillo. Asi el
    // acento BLANCO produce un halo blanco/gris (no rojo).
    private static Color Derive(Color c, double valueFactor)
    {
        RgbToHsv(c, out var h, out var s, out var v);
        var glowSaturation = s < 0.15 ? s : 1.0;
        return HsvToRgb(h, glowSaturation, Math.Clamp(v * valueFactor, 0, 1));
    }

    private static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0)
        {
            h = 0;
            return;
        }

        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * (((b - r) / d) + 2);
        else h = 60 * (((r - g) / d) + 4);
        if (h < 0) h += 360;
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromArgb(255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
