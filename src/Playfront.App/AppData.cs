using System;
using System.IO;

namespace Playfront.App;

/// <summary>
/// La carpeta donde Playfront guarda lo suyo en la maquina del usuario
/// (%LocalAppData%\Playfront): color de acento, fondo elegido, datos de YouTube y el registro.
/// Se obtiene por la API de Windows, nunca escribiendo "C:\Users\..." a mano, porque la ruta cambia
/// segun la cuenta y la maquina (norma de distribucion).
/// </summary>
internal static class AppData
{
    private const string FolderName = "Playfront";

    /// <summary>Nombre que tenia la carpeta antes de renombrar el proyecto (2026-07-26).</summary>
    private const string LegacyFolderName = "Atlas";

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>Ruta a un fichero dentro de la carpeta de datos.</summary>
    public static string File(string name) => Path.Combine(Folder, name);

    /// <summary>
    /// Traslada los datos de la carpeta antigua ("Atlas") a la nueva ("Playfront") la primera vez que
    /// se arranca despues del cambio de nombre, para que nadie pierda sus ajustes.
    ///
    /// Llamar ANTES de que cualquier otra cosa toque la carpeta de datos (lo primero de Program.Main):
    /// varias clases calculan su ruta una sola vez, asi que moverla despues no serviria de nada.
    ///
    /// Solo actua si existe la antigua y NO existe la nueva; si ya hay datos nuevos, no toca nada
    /// (nunca sobrescribe). Si el traslado falla, se sigue adelante con la carpeta nueva vacia: perder
    /// unas preferencias es molesto, no arrancar es inaceptable (degradar, nunca reventar).
    /// </summary>
    public static void MigrateLegacyFolder()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName);

            if (!Directory.Exists(legacy) || Directory.Exists(Folder))
            {
                return;
            }

            Directory.Move(legacy, Folder);

            // El registro tambien cambio de nombre (atlas.log -> playfront.log). Se renombra para no
            // dejar dos ficheros y perder el historial de arranques anteriores.
            // "global::" porque la app tiene su propio espacio de nombres System (la carpeta System\),
            // asi que un "System.IO" a secas se buscaria ahi dentro y no en .NET.
            var legacyLog = Path.Combine(Folder, "atlas.log");
            var newLog = Path.Combine(Folder, "playfront.log");
            if (global::System.IO.File.Exists(legacyLog) && !global::System.IO.File.Exists(newLog))
            {
                global::System.IO.File.Move(legacyLog, newLog);
            }
        }
        catch
        {
            // Sin permisos, carpeta en uso, disco lleno... se arranca igual, con datos nuevos.
        }
    }
}
