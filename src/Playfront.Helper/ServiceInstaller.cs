using System.Diagnostics;

namespace Playfront.Helper;

// Registro/baja del ayudante como servicio de Windows, via sc.exe. Requiere permisos de administrador
// (crear un servicio SYSTEM los exige) — ese es el UNICO momento en que hace falta elevacion, una sola
// vez. Despues el servicio ya corre como SYSTEM y todo lo que hace es sin mas avisos (igual que
// GamingServices de Xbox).
internal static class ServiceInstaller
{
    public const string ServiceName = "PlayfrontHelper";
    private const string DisplayName = "Playfront Helper";
    private const string Description = "Playfront privileged helper (installs Steam, and later TDP/performance profiles).";

    public static int Install()
    {
        // Ruta al PROPIO ejecutable (el apphost Playfront.Helper.exe). OJO: hay que ejecutar el .exe
        // directamente para instalar, no via "dotnet run" (si no, se registraria dotnet.exe).
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Ejecuta el propio Playfront.Helper.exe para instalar (no 'dotnet run').");
            return 1;
        }

        // sc create: el espacio tras "binPath=" es OBLIGATORIO; la ruta va entre comillas por si tiene
        // espacios. start= auto para que arranque con Windows.
        var r = Run("sc", $"create {ServiceName} binPath= \"{exe}\" start= auto DisplayName= \"{DisplayName}\"");
        if (r != 0) return r;
        Run("sc", $"description {ServiceName} \"{Description}\"");
        Run("sc", $"start {ServiceName}");
        return 0;
    }

    public static int Uninstall()
    {
        Run("sc", $"stop {ServiceName}");
        return Run("sc", $"delete {ServiceName}");
    }

    public static int Status() => Run("sc", $"query {ServiceName}");

    private static int Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null)
        {
            Console.Error.WriteLine($"No se pudo lanzar {file}.");
            return 1;
        }
        Console.Write(p.StandardOutput.ReadToEnd());
        Console.Write(p.StandardError.ReadToEnd());
        p.WaitForExit();
        return p.ExitCode;
    }
}
