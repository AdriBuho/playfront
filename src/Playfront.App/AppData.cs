using System;
using System.IO;

namespace Playfront.App;

/// <summary>
/// Where Playfront keeps its per-machine data (%LocalAppData%\Playfront): accent colour, chosen
/// background, YouTube session and the log. Resolved through the Windows API, never by writing
/// "C:\Users\..." by hand — the path varies per account and machine.
/// </summary>
internal static class AppData
{
    private const string FolderName = "Playfront";

    /// <summary>Folder name used before the project was renamed.</summary>
    private const string LegacyFolderName = "Atlas";

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>Path to a file inside the data folder.</summary>
    public static string File(string name) => Path.Combine(Folder, name);

    /// <summary>
    /// Moves data from the old folder to the current one on the first run after the rename, so nobody
    /// loses their settings.
    ///
    /// Must be called before anything else touches the data folder — first thing in Program.Main.
    /// Several classes resolve their path once, so moving it afterwards would have no effect.
    ///
    /// Only acts when the old folder exists and the new one does not; it never overwrites. If the move
    /// fails, startup continues with an empty new folder: losing preferences is annoying, failing to
    /// start is not acceptable.
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

            // The log was renamed too (atlas.log -> playfront.log). Renaming it keeps the history of
            // previous startups instead of leaving two files behind.
            // "global::" is required because the app has its own System namespace (the System\ folder),
            // so a bare "System.IO" would resolve inside it rather than in .NET.
            var legacyLog = Path.Combine(Folder, "atlas.log");
            var newLog = Path.Combine(Folder, "playfront.log");
            if (global::System.IO.File.Exists(legacyLog) && !global::System.IO.File.Exists(newLog))
            {
                global::System.IO.File.Move(legacyLog, newLog);
            }
        }
        catch
        {
            // No permissions, folder in use, disk full... start anyway with fresh data.
        }
    }
}
