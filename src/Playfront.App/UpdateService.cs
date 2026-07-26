using System;
using System.IO;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Playfront.App;

/// <summary>
/// El motor del boton de "actualizar" (estilo Xbox): comprobar si hay version nueva, descargarla con
/// progreso, y reiniciar en la version nueva. Es SOLO la logica: no dibuja nada, para que la pantalla
/// que la use (System -> Console info, cuando exista) sea la unica dueña de como se ve.
///
/// Por debajo usa Velopack (MIT). Detalles que condicionan el diseño:
///  - Velopack instala en la carpeta del usuario, asi que actualizar NO pide permisos de administrador.
///  - Solo funciona si la app se instalo con su instalador. Ejecutandola desde la carpeta de
///    compilacion (o desde una copia a mano) no hay nada que actualizar: eso NO es un error, es
///    <see cref="UpdateState.Unsupported"/>, y la interfaz tiene que decirlo en pantalla en vez de
///    callarse (norma de distribucion: degradar, nunca reventar, y explicar por que).
/// </summary>
internal enum UpdateState
{
    /// <summary>Sin comprobar todavia.</summary>
    Idle,

    /// <summary>Preguntando si hay version nueva.</summary>
    Checking,

    /// <summary>No se puede actualizar desde aqui: la app no esta instalada con el instalador.</summary>
    Unsupported,

    /// <summary>Comprobado: es la ultima version.</summary>
    UpToDate,

    /// <summary>Hay version nueva, sin descargar.</summary>
    Available,

    /// <summary>Descargando (mirar <see cref="UpdateService.Progress"/>).</summary>
    Downloading,

    /// <summary>Descargada y lista: al reiniciar se aplica.</summary>
    ReadyToRestart,

    /// <summary>Algo fallo (sin red, la fuente no responde, disco lleno...). Ver <see cref="UpdateService.LastError"/>.</summary>
    Failed,
}

internal sealed class UpdateService
{
    /// <summary>
    /// De donde salen las actualizaciones. GitHub Releases porque no tiene tope de descargas ni de
    /// tamaño y es gratis en repositorios publicos.
    ///
    /// Tiene que coincidir EXACTAMENTE con el repositorio real. Si no coincide, no da error visible:
    /// la comprobacion simplemente no encuentra nada y la app se queda callada creyendo que ya esta
    /// al dia. Al renombrar o mover el repositorio, cambiar tambien esto.
    /// </summary>
    private const string GithubRepository = "https://github.com/AdriBuho/playfront";

    /// <summary>
    /// Valvula de escape para PROBAR en local: si esta puesta, las actualizaciones se buscan ahi
    /// (una carpeta del disco o una URL) en vez de en GitHub. Mismo patron que
    /// PLAYFRONT_CAPTURE_POSTER en Program.cs: nada de codigo de prueba dentro del producto.
    /// </summary>
    private const string SourceOverrideVariable = "PLAYFRONT_UPDATE_SOURCE";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public UpdateService()
    {
        // Construir el UpdateManager no toca la red; solo mira como esta instalada la app.
        // Aun asi puede fallar (una instalacion a medias, permisos raros), y eso no debe impedir
        // que la app arranque: se queda sin actualizaciones y lo dice.
        try
        {
            var over = Environment.GetEnvironmentVariable(SourceOverrideVariable);
            IUpdateSource source = string.IsNullOrWhiteSpace(over)
                ? new GithubSource(GithubRepository, accessToken: null, prerelease: false)
                : new SimpleFileSource(new DirectoryInfo(over));

            _manager = new UpdateManager(source);
        }
        catch (Exception e)
        {
            CrashLog.Log("UpdateService (init)", e);
            _manager = null;
        }
    }

    /// <summary>En que punto esta. La interfaz pinta a partir de esto.</summary>
    public UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>Progreso de descarga, 0-100. Solo tiene sentido en <see cref="UpdateState.Downloading"/>.</summary>
    public int Progress { get; private set; }

    /// <summary>Version que se instalaria, si hay alguna. Ejemplo: "0.1.1".</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Por que fallo, en texto para el usuario (en ingles: norma de idioma de la interfaz).</summary>
    public string? LastError { get; private set; }

    /// <summary>Se dispara en cada cambio de estado o de progreso, para que la interfaz se refresque.</summary>
    public event Action? Changed;

    /// <summary>
    /// Version instalada segun Velopack. Puede no coincidir con <see cref="PlayfrontVersion.Current"/>
    /// si la app no se instalo con el instalador (ahi devuelve null).
    /// </summary>
    public string? InstalledVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>Lo que ensena el boton de "buscar actualizaciones".</summary>
    public async Task CheckAsync()
    {
        if (_manager is null || !_manager.IsInstalled)
        {
            // Caso normalisimo en desarrollo (dotnet run) y en una copia de la carpeta publicada.
            LastError = "Updates are only available when Playfront is installed with its installer.";
            Set(UpdateState.Unsupported);
            return;
        }

        Set(UpdateState.Checking);
        try
        {
            _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pending is null)
            {
                AvailableVersion = null;
                Set(UpdateState.UpToDate);
                CrashLog.Info($"Updates: ya esta en la ultima version ({InstalledVersion}).");
                return;
            }

            AvailableVersion = _pending.TargetFullRelease.Version.ToString();
            Set(UpdateState.Available);
            CrashLog.Info($"Updates: hay version nueva {AvailableVersion} (instalada {InstalledVersion}).");
        }
        catch (Exception e)
        {
            Fail("Couldn't check for updates. Check your internet connection.", e, "UpdateService (check)");
        }
    }

    /// <summary>Descarga la version nueva. Deja la app usable mientras baja.</summary>
    public async Task DownloadAsync()
    {
        if (_manager is null || _pending is null) return;

        Progress = 0;
        Set(UpdateState.Downloading);
        try
        {
            await _manager.DownloadUpdatesAsync(_pending, p =>
            {
                Progress = p;
                Changed?.Invoke();
            }).ConfigureAwait(false);

            Set(UpdateState.ReadyToRestart);
            CrashLog.Info($"Updates: {AvailableVersion} descargada, lista para aplicar al reiniciar.");
        }
        catch (Exception e)
        {
            Fail("Couldn't download the update.", e, "UpdateService (download)");
        }
    }

    /// <summary>
    /// Aplica lo descargado y reinicia Playfront. NO vuelve: el proceso muere aqui.
    /// Ojo cuando Playfront sea el shell: durante el reinicio no hay interfaz ninguna en pantalla.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _pending is null || State != UpdateState.ReadyToRestart) return;

        try
        {
            CrashLog.Info($"Updates: aplicando {AvailableVersion} y reiniciando.");
            _manager.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception e)
        {
            Fail("Couldn't apply the update.", e, "UpdateService (apply)");
        }
    }

    private void Fail(string userMessage, Exception e, string context)
    {
        CrashLog.Log(context, e);
        LastError = userMessage;
        Set(UpdateState.Failed);
    }

    private void Set(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed && state != UpdateState.Unsupported) LastError = null;
        Changed?.Invoke();
    }
}
