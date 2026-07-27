using System;
using System.IO;
using System.Threading.Tasks;

namespace Playfront.App;

/// <summary>
/// Crash safety net: logging plus per-surface guards. Playfront is meant to replace explorer.exe as
/// the shell, where an unhandled exception leaves the console staring at a black screen. So:
///  - <see cref="Guard"/> wraps every UI-thread surface (timer ticks, input, callbacks) so a one-off
///    failure is logged and the app stays alive instead of dying from something recoverable.
///  - <see cref="Install"/> hooks .NET's global events only to LOG whatever escapes. It does not
///    recover: on modern .NET those fire with the process already going down. Recovery is the
///    external watchdog's job.
/// The log lives at %LocalAppData%\Playfront\playfront.log.
/// </summary>
internal static class CrashLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = BuildLogPath();

    private static string BuildLogPath()
    {
        var dir = AppData.Folder;
        try { Directory.CreateDirectory(dir); } catch { /* if it can't be created, Log swallows it */ }
        return Path.Combine(dir, "playfront.log");
    }

    // Hooks .NET's global handlers. Call once, as early as possible (Program.Main).
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain.UnhandledException" + (e.IsTerminating ? " (terminating)" : ""), e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    // Runs an action, logging any exception. The failure does not propagate: the app stays alive.
    // Use on every UI-thread surface that would otherwise take everything down (ticks, input...).
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

    // Records an informational entry in the same log. With no telemetry at all, this local log is the
    // only way to diagnose an "it broke" report from someone else's machine, so it should at least say
    // which version started and when.
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
            // Logging itself must never take the app down.
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
            // Logging itself must never take the app down (disk full, permissions, etc.).
        }
    }
}
