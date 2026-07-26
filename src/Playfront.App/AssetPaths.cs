using System;
using System.IO;

namespace Playfront.App;

/// <summary>
/// Donde estan los assets PESADOS (los fondos de vídeo y el arte de juegos, ~416 MB).
///
/// Van aparte del programa a proposito, y esto es una decision de arquitectura, no un detalle:
///  - **El sistema de actualizacion no los toca.** Velopack reemplaza la carpeta del programa en cada
///    actualizacion; si los assets vivieran ahi dentro, cada actualizacion arrastraria 416 MB en vez
///    de los ~70 KB que cuesta hoy.
///  - **Pueden ser opcionales.** El instalador puede ofrecer descargarlos o no.
///  - **Se puede dejar de repartir un fichero concreto** (si una editora lo reclama) sin que se rompa
///    ninguna instalacion.
///
/// Orden de busqueda, y por que en ese orden:
///  1. **Junto al ejecutable** (`&lt;exe&gt;\Assets\Backgrounds`). Es el caso de desarrollo y el de una
///     carpeta publicada que se copia a mano. Va primero para que trabajar en el repositorio se
///     comporte siempre igual, aunque la maquina tenga tambien una instalacion de verdad.
///  2. **La carpeta compartida de la maquina** (`%ProgramData%\Playfront\Assets\Backgrounds`), que es
///     donde los deja el instalador. Es compartida (no por usuario) para no duplicar 416 MB si hay
///     varias cuentas, y de solo lectura para la app: escribir ahi es cosa del instalador, que va
///     elevado.
///
/// Si no aparecen en ningun sitio, la app arranca igual (verificado el 2026-07-26): fondo negro y los
/// tiles como rectangulos grises. Eso es lo que hace posible retirar un fichero sin romper nada.
/// </summary>
internal static class AssetPaths
{
    /// <summary>Subcarpeta compartida donde el instalador deja los assets pesados.</summary>
    private const string SharedSubfolder = @"Playfront\Assets";

    private static readonly string LocalRoot =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds");

    private static readonly string SharedRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        SharedSubfolder, "Backgrounds");

    /// <summary>
    /// Ruta completa en disco de un asset de fondos, a partir de su ruta relativa
    /// (por ejemplo "Games/halo.mp4"). Devuelve la primera que exista de verdad.
    ///
    /// Si no existe en ningun sitio devuelve la ruta local, para que quien la use vea "no esta" en el
    /// sitio esperado en vez de una ruta rara del sistema — el mensaje de error importa cuando esto
    /// falla en la maquina de otra persona y solo tenemos el registro para diagnosticar.
    /// </summary>
    public static string Background(string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);

        var local = Path.Combine(LocalRoot, rel);
        if (File.Exists(local)) return local;

        var shared = Path.Combine(SharedRoot, rel);
        if (File.Exists(shared)) return shared;

        return local;
    }

    /// <summary>
    /// Si los assets pesados estan disponibles en algun sitio. Sirve para que la interfaz pueda
    /// EXPLICAR por que esta todo vacio en vez de callarse (norma de "degradar, nunca reventar").
    /// </summary>
    public static bool HeavyAssetsAvailable =>
        Directory.Exists(Path.Combine(LocalRoot, "Games")) ||
        Directory.Exists(Path.Combine(SharedRoot, "Games"));

    /// <summary>Las dos rutas donde se busca, para escribirlas en el registro al arrancar.</summary>
    public static string Describe() => $"assets locales='{LocalRoot}', compartidos='{SharedRoot}'";
}
