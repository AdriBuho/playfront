using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Playfront.Helper;

// Installs Steam from the helper (SYSTEM). Since the helper is already privileged, no UAC prompt
// appears. It downloads Valve's OFFICIAL installer, verifies its signature — mandatory, because this
// runs elevated — and executes it silently.
internal static class SteamInstaller
{
    // Valve's official Steam bootstrapper (signed, ~3 MB, always the latest version).
    private const string InstallerUrl = "https://cdn.cloudflare.steamstatic.com/client/installer/SteamSetup.exe";

    // The helper runs as SYSTEM, so detection reads the MACHINE hive (HKLM): HKCU from a SYSTEM service
    // would be SYSTEM's own hive, not the user's. Steam's installer writes InstallPath machine-wide.
    public static (bool installed, string? path) Detect()
    {
        var path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = @"C:\Program Files (x86)\Steam"; // installer default
        }
        var exe = Path.Combine(path, "steam.exe");
        return File.Exists(exe) ? (true, path) : (false, null);
    }

    // dryRun = download and verify the signature but do NOT execute, for testing without touching
    //          anything. force = run the installer even if Steam is already present; re-running only
    //          repairs it, which is useful to exercise the whole chain without uninstalling.
    public static async Task<(bool ok, string message)> InstallAsync(bool dryRun, bool force, ILogger log)
    {
        var (installed, existingPath) = Detect();
        if (installed && !force && !dryRun)
        {
            return (true, $"Steam is already installed ({existingPath}).");
        }

        var tmp = Path.Combine(Path.GetTempPath(), "PlayfrontSteamSetup.exe");
        log.LogInformation("Downloading the Steam installer to {Tmp}", tmp);
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
            return (false, $"Couldn't download the Steam installer: {e.Message}");
        }

        var sizeKb = new FileInfo(tmp).Length / 1024;

        // Mandatory before running anything elevated: valid signature, and the signer must be Valve.
        if (!SignatureVerifier.VerifyValve(tmp, out var signer))
        {
            TryDelete(tmp);
            return (false, $"Installer signature invalid or not from Valve (signer: {signer}). Aborted for safety.");
        }
        log.LogInformation("Installer downloaded ({SizeKb} KB), signature verified: {Signer}", sizeKb, signer);

        if (dryRun)
        {
            TryDelete(tmp);
            return (true, $"OK (dry run): downloaded {sizeKb} KB, SIGNATURE VERIFIED — signed by [{signer}]. Not executed.");
        }

        // Silent install (/S). No UAC prompt because the helper is already SYSTEM.
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tmp, "/S") { UseShellExecute = false })!;
            await p.WaitForExitAsync();
        }
        catch (Exception e)
        {
            return (false, $"The Steam installer failed to run: {e.Message}");
        }
        finally
        {
            TryDelete(tmp);
        }

        var (nowInstalled, newPath) = Detect();
        return nowInstalled
            ? (true, $"Steam installed successfully ({newPath}).")
            : (false, "The installer finished but Steam isn't detected yet (on first run Steam updates itself and needs internet).");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}
