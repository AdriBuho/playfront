using System;
using System.IO;

namespace Playfront.App;

// Gestiona el fondo de la home. Tres posibilidades: el video dinamico por defecto, uno de los 14
// colores solidos del selector "Solid colors", o un video concreto elegido en "Dynamic backgrounds"
// (p.ej. Modern Warfare III). La eleccion se guarda en disco y se recarga al arrancar, igual que
// AccentTheme hace con el color de acento. Un archivo aparte del acento (background.txt) porque son
// ajustes independientes: el color del tema (bordes/selecciones) y el fondo de pantalla no tienen
// por que coincidir.
public static class BackgroundSettings
{
    // Los 14 colores solidos, calcados PIXEL A PIXEL del centro de cada recuadro del frame del video
    // ("How to Change your Background on Xbox Series X", pantalla "Solid colors", ~segundo 31). Fila
    // 1 (indices 0..6) y luego fila 2 (7..13), en el mismo orden que la rejilla. Los recuadros de
    // Xbox tienen un degradado vertical muy sutil; se toma el color del centro como representativo
    // (igual que el selector de acento, que usa recuadros de color plano).
    public static readonly string[] SolidPalette =
    {
        "#101010", "#389517", "#0F7464", "#0E70B3", "#14347A", "#852991", "#940F5C",
        "#3A3A3A", "#106C0F", "#0F6B6E", "#115CA8", "#5E3398", "#B8315E", "#780A1B",
    };

    // Valor guardado: un hex "#RRGGBB" = ese color solido; "video:<ruta relativa a Assets/Backgrounds>"
    // = ese video concreto; cualquier otra cosa (o nada) = video dinamico por defecto.
    private static string SettingsPath => AppData.File("background.txt");

    // Devuelve el hex del color solido guardado, o null si el fondo es el video dinamico (por
    // defecto, o si no se puede leer el archivo).
    public static string? LoadSolidHex()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var value = File.ReadAllText(SettingsPath).Trim();
                if (value.StartsWith("#", StringComparison.Ordinal))
                {
                    return value;
                }
            }
        }
        catch
        {
            // Si el archivo esta corrupto o no se puede leer, se cae al video dinamico.
        }

        return null;
    }

    // Devuelve la ruta (relativa a Assets/Backgrounds) del video dinamico concreto elegido, o null si
    // el fondo no es un video concreto (color solido, o el video dinamico por defecto).
    public static string? LoadVideoRelPath()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var value = File.ReadAllText(SettingsPath).Trim();
                if (value.StartsWith("video:", StringComparison.Ordinal))
                {
                    return value["video:".Length..];
                }
            }
        }
        catch
        {
            // Si no se puede leer, se cae al video dinamico por defecto.
        }

        return null;
    }

    public static void SaveSolid(string hex) => Write(hex);

    public static void SaveVideo(string relPath) => Write("video:" + relPath);

    public static void SaveDynamic() => Write("dynamic");

    private static void Write(string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, value);
        }
        catch
        {
            // Guardar es best-effort (igual que AccentTheme): si falla, el fondo sigue aplicado en
            // esta sesion, solo no persiste al reiniciar.
        }
    }
}
