using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Playfront.App.Library;

/// <summary>
/// Installs third-party Windows applications through winget, the package manager that ships with
/// Windows 10 1809 and later. Used for products Playfront offers but does not build - Spotify is the
/// first one.
///
/// TWO TRAPS, both hit while building this:
///
/// 1. SPOTIFY'S INSTALLER REFUSES TO RUN ELEVATED. It aborts with "The installer cannot be run from
///    an administrator context". So this must never be called from an elevated process, which is why
///    installing happens from the APP (normal privileges) and never from Playfront's own installer,
///    which runs elevated. <see cref="IsElevated"/> checks for it and reports instead of failing
///    with an unexplained exit code.
///
/// 2. winget is an "app execution alias" - a stub in WindowsApps that Windows resolves. Launching it
///    by bare name works, but the full path is used when the bare name is not found, because a
///    non-interactive or reduced environment does not always carry that folder on PATH.
/// </summary>
public static class WingetInstaller
{
    /// <summary>Result of an install attempt, with enough detail to say WHY on screen.</summary>
    public sealed record Result(bool Ok, string Message);

    /// <summary>True when this process runs with administrator rights.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false; // cannot tell: assume not, the install will report the real error
        }
    }

    /// <summary>Full path to winget, or null when it is not on this machine.</summary>
    public static string? Find()
    {
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");

        return File.Exists(alias) ? alias : null;
    }

    /// <summary>
    /// Installs a package by its winget id. Blocks on a background thread; the caller awaits it.
    /// </summary>
    public static async Task<Result> InstallAsync(string packageId, CancellationToken cancel = default)
    {
        if (IsElevated())
        {
            // Better to say this than to let the installer fail with its own message: from here we
            // know exactly why, and the user cannot act on winget's wording.
            return new Result(false, "Playfront is running as administrator, and app installers refuse that. Start Playfront normally.");
        }

        var winget = Find();
        if (winget is null)
        {
            return new Result(false, "winget is not available on this machine.");
        }

        var psi = new ProcessStartInfo(winget)
        {
            // --silent: no installer window. The agreements flags stop it from waiting on a prompt
            // nobody can answer, since there is no console attached.
            Arguments = $"install --id {packageId} --exact --silent " +
                        "--accept-package-agreements --accept-source-agreements " +
                        "--disable-interactivity",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return new Result(false, "Could not start winget.");
            }

            // Output is drained even though it is not shown: a full pipe buffer deadlocks the child.
            var stdout = p.StandardOutput.ReadToEndAsync(cancel);
            var stderr = p.StandardError.ReadToEndAsync(cancel);
            await p.WaitForExitAsync(cancel);

            if (p.ExitCode == 0)
            {
                return new Result(true, "Installed.");
            }

            var salida = (await stdout + "\n" + await stderr).Trim();
            CrashLog.Log($"winget install {packageId} failed ({p.ExitCode}): {salida}", null);
            return new Result(false, $"The install did not finish (code {p.ExitCode}).");
        }
        catch (OperationCanceledException)
        {
            return new Result(false, "Cancelled.");
        }
        catch (Exception e)
        {
            CrashLog.Log($"winget install {packageId} threw", e);
            return new Result(false, e.Message);
        }
    }
}
