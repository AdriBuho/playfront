using Avalonia;
using System;
using System.Runtime.InteropServices;
using Velopack;

namespace Playfront.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack (el motor de actualizaciones) tiene que ir en la PRIMERA linea de Main, y no es un
        // capricho de su documentacion: justo despues de instalar o de actualizar, el instalador
        // relanza este mismo .exe con argumentos especiales para crear el acceso directo, limpiar la
        // version anterior, etc. Esta llamada atiende esas peticiones y TERMINA el proceso sin abrir
        // ninguna ventana. Si se arrancara la interfaz antes, en cada instalacion y en cada
        // actualizacion se veria aparecer Playfront un instante y cerrarse solo.
        // En un arranque normal no hace nada y sigue de largo.
        VelopackApp.Build().Run();

        // LO PRIMERO de todo: si venimos de la epoca en que el proyecto se llamaba Atlas, mover los
        // datos del usuario (%LocalAppData%\Atlas -> ...\Playfront) para no perder sus ajustes. Tiene
        // que ir aqui arriba porque varias clases calculan su ruta una sola vez, la primera vez que se
        // usan: moverla despues no tendria ningun efecto.
        AppData.MigrateLegacyFolder();

        // Modo oculto de captura de poster: decodifica el primer fotograma de un video con el MISMO
        // decodificador que lo reproduce (Media Foundation), lo escribe en crudo y sale, SIN arrancar
        // la interfaz. Sirve para generar posters que coinciden en color con el video (ver
        // Video/PosterCapture.cs). Formato de la variable: "rutaVideo|rutaSalidaCruda".
        var capture = Environment.GetEnvironmentVariable("PLAYFRONT_CAPTURE_POSTER");
        if (!string.IsNullOrEmpty(capture))
        {
            var parts = capture.Split('|');
            var (w, h) = Playfront.App.Video.PosterCapture.CaptureFirstFrameRaw(parts[0], parts[1]);
            Console.WriteLine($"CAPTURED {w}x{h} -> {parts[1]}");
            return;
        }

        // Red de seguridad (P0): engancha el registro global de errores lo antes posible, y protege
        // el arranque/bucle principal. Si algo fatal tumba la app, queda registrado el motivo en
        // %LocalAppData%\Playfront\playfront.log y el proceso muere (mas adelante el vigilante externo lo
        // relanzara; la recuperacion real es cosa suya, no de aqui).
        CrashLog.Install();

        // Primera linea de cada arranque: que version es y sobre que Windows corre. Es la base del
        // "informe de diagnostico" que pide la norma de distribucion - si alguien reporta un fallo,
        // esto ya dice con que version le paso, sin necesidad de preguntarselo.
        CrashLog.Info($"Playfront {PlayfrontVersion.Current} arrancando en Windows {Environment.OSVersion.Version} ({RuntimeInformation.ProcessArchitecture})");

        // Si faltan los assets pesados (los ~416 MB de fondos y arte), la app funciona igual pero se ve
        // vacia. Queda escrito para que un "se me ve todo gris" se diagnostique sin adivinar.
        if (!AssetPaths.HeavyAssetsAvailable)
        {
            CrashLog.Info($"Assets pesados NO encontrados: la interfaz se vera sin fondos ni arte. Buscados en {AssetPaths.Describe()}.");
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            CrashLog.Log("FATAL (Main)", e);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // AngleEgl es el modo de dibujado que usa Direct3D 11 por debajo (en vez del modo
            // por defecto) - hace falta para que el fondo de video por hardware pueda entregarle
            // fotogramas a Avalonia sin pasar por la memoria normal (ver Video/HardwareVideoBackgroundControl.cs).
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl] })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
