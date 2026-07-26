using System;
using System.IO;
using System.Threading.Tasks;

namespace Playfront.App;

/// <summary>
/// Red de seguridad ante caidas (P0 de la auditoria), capa de REGISTRO + guardas por superficie.
/// Playfront va a ser el shell (sustituye a explorer.exe): un fallo no capturado deja la consola en negro,
/// asi que:
///  - <see cref="Guard"/> envuelve cada superficie del hilo de UI (ticks de temporizador, input,
///    callbacks) para que un fallo PUNTUAL se registre y la app SIGA viva, en vez de morir por algo
///    de lo que se podria haber recuperado.
///  - <see cref="Install"/> engancha los eventos globales de .NET solo para REGISTRAR lo que se escape
///    (NO recupera: en .NET moderno saltan con el proceso ya muriendo; la recuperacion real la dara el
///    vigilante externo, la otra pieza de P0).
/// El log se escribe en %LocalAppData%\Playfront\playfront.log.
/// </summary>
internal static class CrashLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = BuildLogPath();

    private static string BuildLogPath()
    {
        var dir = AppData.Folder;
        try { Directory.CreateDirectory(dir); } catch { /* si no se puede crear, Log lo ignora */ }
        return Path.Combine(dir, "playfront.log");
    }

    // Engancha los gestores globales de .NET. Llamar UNA vez, lo antes posible (Program.Main).
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain.UnhandledException" + (e.IsTerminating ? " (terminando)" : ""), e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    // Ejecuta una accion capturando y registrando cualquier excepcion. El fallo NO se propaga: la app
    // sigue viva. Usar en cada superficie del hilo de UI que, de fallar, tumbaria todo (ticks, input...).
    public static void Guard(Action action, string context)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log(context, e);
        }
    }

    // Apunta un dato informativo (no un fallo) en el mismo registro. Existe porque, sin telemetria
    // ninguna, el registro local es LO UNICO con lo que se puede diagnosticar un "se me ha roto" en
    // la maquina de otra persona: conviene que diga al menos que version arranco y cuando.
    public static void Info(string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [info] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // El propio registro nunca debe tumbar la app.
        }
    }

    public static void Log(string context, Exception? e)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{context}] {e}{Environment.NewLine}");
            }
        }
        catch
        {
            // El propio registro nunca debe tumbar la app (disco lleno, permisos, etc.).
        }
    }
}
