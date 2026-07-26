using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Playfront.Helper;

// Instala Steam desde el ayudante (SYSTEM). Como el ayudante ya es privilegiado, NO aparece UAC (igual
// que la app de Xbox instalando via GamingServices). Descarga el instalador OFICIAL de Valve, verifica
// su firma (obligatorio, se ejecuta elevado) y lo corre en silencio.
internal static class SteamInstaller
{
    // URL oficial del stub de instalacion de Steam (firmado por Valve, ~3 MB, siempre ultima version).
    private const string InstallerUrl = "https://cdn.cloudflare.steamstatic.com/client/installer/SteamSetup.exe";

    // El ayudante corre como SYSTEM, asi que para detectar Steam se usa el registro de MAQUINA (HKLM):
    // HKCU desde un servicio SYSTEM seria la colmena de SYSTEM, no la del usuario. InstallPath lo escribe
    // el instalador de Steam a nivel de maquina.
    public static (bool installed, string? path) Detect()
    {
        var path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = @"C:\Program Files (x86)\Steam"; // ruta por defecto del instalador
        }
        var exe = Path.Combine(path, "steam.exe");
        return File.Exists(exe) ? (true, path) : (false, null);
    }

    // dryRun = descarga y verifica la firma pero NO ejecuta (para probar sin tocar nada).
    // force   = ejecuta el instalador aunque Steam ya este (re-ejecutarlo solo lo repara; util para
    //           probar la cadena completa sin desinstalar).
    public static async Task<(bool ok, string message)> InstallAsync(bool dryRun, bool force, ILogger log)
    {
        var (installed, existingPath) = Detect();
        if (installed && !force && !dryRun)
        {
            return (true, $"Steam ya esta instalado ({existingPath}).");
        }

        var tmp = Path.Combine(Path.GetTempPath(), "PlayfrontSteamSetup.exe");
        log.LogInformation("Descargando el instalador de Steam a {Tmp}", tmp);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var resp = await http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await resp.Content.CopyToAsync(fs);
        }
        catch (Exception e)
        {
            return (false, $"No se pudo descargar el instalador de Steam: {e.Message}");
        }

        var sizeKb = new FileInfo(tmp).Length / 1024;

        // OBLIGATORIO antes de ejecutar nada elevado: firma valida + firmante Valve.
        if (!SignatureVerifier.VerifyValve(tmp, out var signer))
        {
            TryDelete(tmp);
            return (false, $"Firma del instalador NO valida o no es de Valve (firmante: {signer}). Abortado por seguridad.");
        }
        log.LogInformation("Instalador descargado ({SizeKb} KB) y firma verificada: {Signer}", sizeKb, signer);

        if (dryRun)
        {
            TryDelete(tmp);
            return (true, $"OK (prueba en seco): descargado {sizeKb} KB y FIRMA VERIFICADA — firmado por [{signer}]. No se ejecuto.");
        }

        // Ejecutar en silencio (/S). Sin UAC porque el ayudante ya es SYSTEM.
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tmp, "/S") { UseShellExecute = false })!;
            await p.WaitForExitAsync();
        }
        catch (Exception e)
        {
            return (false, $"El instalador de Steam fallo al ejecutarse: {e.Message}");
        }
        finally
        {
            TryDelete(tmp);
        }

        var (nowInstalled, newPath) = Detect();
        return nowInstalled
            ? (true, $"Steam instalado correctamente ({newPath}).")
            : (false, "El instalador termino pero Steam aun no se detecta (la primera vez Steam se actualiza al arrancar y necesita internet).");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* limpieza best-effort */ }
    }
}
