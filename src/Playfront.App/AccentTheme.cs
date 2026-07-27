using System;
using System.IO;
using Avalonia;
using Avalonia.Media;

namespace Playfront.App;

// Theme accent colour: everything that marks selection (borders, rings, glows, highlights, the "My
// color" swatch). It used to be a fixed StaticResource with the glows hard-coded in XAML; now it all
// derives from ONE base colour published as application resources that XAML consumes through
// DynamicResource, so the picker can change it live. The choice is persisted and reloaded at startup.
public static class AccentTheme
{
    // Default accent: "Bright green" (#5AA029), the green at grid position r1c1. Deliberately one of
    // the 14 so that opening the picker for the first time already shows a grid entry marked as
    // applied. Closest match to the previously approved #439941.
    public const string DefaultHex = "#5AA029";

    // The 14 picker colours with a display name for "My color". Same order as the grid: row 1, then
    // row 2. Only "Bright green" (r1c1) is a confirmed Xbox name; the rest are reasonable descriptions
    // and can be corrected here if the real ones turn up.
    //
    // Two things not to "fix":
    //  - "Navy" (#25458B, the darkest blue) was replaced by WHITE on purpose, so that picking it gives
    //    a white selection like the Xbox Store.
    //  - Order runs lightest to darkest by perceived luminance (0.299R+0.587G+0.114B), starting at
    //    white. ColorSwatchHexes in MainWindow uses EXACTLY the same order — same index, same colour.
    public static readonly (string Hex, string Name)[] Palette =
    {
        ("#FFFFFF", "White"),     ("#DB5985", "Pink"),  ("#5AA029", "Bright green"), ("#D84F1F", "Orange"),
        ("#A64AB3", "Orchid"),    ("#207EBB", "Blue"),  ("#7552A1", "Purple"),
        ("#23807F", "Dark teal"), ("#2073C7", "Azure"), ("#217F72", "Teal"),         ("#D01F2F", "Red"),
        ("#B21F75", "Magenta"),   ("#207A1F", "Forest"),("#991F30", "Crimson"),
    };

    private static string SettingsPath => AppData.File("accent.txt");

    // Display name for an accent colour (the value shown under "My color").
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
            // Corrupt or unreadable: fall back to the default accent.
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
            // Best-effort: on failure the theme still applies for this session, it just won't survive
            // a restart.
        }
    }

    // Publishes every colour, brush and shadow derived from the accent as application resources.
    //
    // The glows are the accent's own hue, darker and more saturated (glow1/2/3, light to dark) — the
    // same relationship the hand-written green had (#439941 -> #007800/#006400/#004B00). The
    // brightness factors 0.783/0.65/0.483 reproduce those three greens from the base accent, so the
    // default theme looks identical to the version that was written by hand.
    public static void Apply(Application app, Color accent)
    {
        var glow1 = Derive(accent, 0.783);
        var glow2 = Derive(accent, 0.65);
        var glow3 = Derive(accent, 0.483);

        var res = app.Resources;
        res["AccentColor"] = accent;
        res["AccentBrush"] = new SolidColorBrush(accent);
        res["AccentFadedBrush"] = new SolidColorBrush(Color.FromArgb(0, accent.R, accent.G, accent.B));
        // Dark version of the accent (same hue) for faint tracks, such as the empty part of the
        // library's storage meter — on Xbox that groove is dark green, not grey. Follows the theme.
        res["AccentTrackBrush"] = new SolidColorBrush(Derive(accent, 0.42));

        // Glows rebuilt in the accent's hue. Blur, spread and alpha are the values each hand-written
        // glow already had; only the RGB changes.
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

    // Glow = same hue, saturation pushed to full, brightness scaled down.
    //
    // Exception for near-greyscale colours (white, grey): saturation is NOT forced, because their hue
    // defaults to 0 — which is red — and a white accent would produce a red glow. Keeping them grey
    // and only dimming gives a white accent a white glow.
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
