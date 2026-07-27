using System.Diagnostics;

namespace Playfront.Helper;

// Registers and removes the helper as a Windows service through sc.exe. Creating a SYSTEM service
// requires administrator rights, and this is the ONLY moment elevation is needed — once. After that
// the service runs as SYSTEM and everything it does happens without further prompts.
internal static class ServiceInstaller
{
    public const string ServiceName = "PlayfrontHelper";
    private const string DisplayName = "Playfront Helper";
    private const string Description = "Playfront privileged helper (installs Steam, and later TDP/performance profiles).";

    public static int Install()
    {
        // Path to this very executable (the Playfront.Helper.exe apphost). It must be run directly to
        // install: through "dotnet run" it would register dotnet.exe as the service binary.
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Run Playfront.Helper.exe itself to install (not 'dotnet run').");
            return 1;
        }

        // sc create: the space after "binPath=" is mandatory, and the path is quoted in case it
        // contains spaces. start= auto so it comes up with Windows.
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
            Console.Error.WriteLine($"Could not start {file}.");
            return 1;
        }
        Console.Write(p.StandardOutput.ReadToEnd());
        Console.Write(p.StandardError.ReadToEnd());
        p.WaitForExit();
        return p.ExitCode;
    }
}
