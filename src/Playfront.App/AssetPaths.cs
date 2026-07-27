using System;
using System.IO;

namespace Playfront.App;

/// <summary>
/// Locates the heavy assets (game background video and artwork, ~416 MB).
///
/// They ship separately from the program on purpose:
///  - The updater never touches them. Velopack replaces the program folder on every update; if the
///    assets lived there, each update would drag 416 MB instead of the ~70 KB it costs today.
///  - They can be optional, so the installer may offer to skip them.
///  - A single file can be withdrawn (if a publisher asks) without breaking any installation.
///
/// Lookup order:
///  1. Next to the executable (`&lt;exe&gt;\Assets\Backgrounds`) — development, and a published folder
///     copied by hand. First so that working in the repository behaves the same way even on a machine
///     that also has a real installation.
///  2. The machine-wide folder (`%ProgramData%\Playfront\Assets\Backgrounds`), where the installer puts
///     them. Machine-wide rather than per-user to avoid duplicating 416 MB across accounts, and
///     read-only to the app: writing there is the installer's job, and it runs elevated.
///
/// With no assets anywhere the app still starts: black background and grey placeholder tiles. That is
/// what makes withdrawing a file safe.
/// </summary>
internal static class AssetPaths
{
    /// <summary>Machine-wide subfolder where the installer puts the heavy assets.</summary>
    private const string SharedSubfolder = @"Playfront\Assets";

    private static readonly string LocalRoot =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds");

    private static readonly string SharedRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        SharedSubfolder, "Backgrounds");

    /// <summary>
    /// Full on-disk path for a background asset given its relative path (for example "Games/halo.mp4").
    /// Returns the first location that actually exists.
    ///
    /// When it exists nowhere, returns the local path so the caller reports "missing" at the expected
    /// location rather than at some system path. That message is all we get when this fails on someone
    /// else's machine.
    /// </summary>
    public static string Background(string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);

        var local = Path.Combine(LocalRoot, rel);
        if (File.Exists(local)) return local;

        var shared = Path.Combine(SharedRoot, rel);
        if (File.Exists(shared)) return shared;

        return local;
    }

    /// <summary>
    /// Whether the heavy assets are available anywhere. Lets the UI explain why everything is empty
    /// instead of staying silent.
    /// </summary>
    public static bool HeavyAssetsAvailable =>
        Directory.Exists(Path.Combine(LocalRoot, "Games")) ||
        Directory.Exists(Path.Combine(SharedRoot, "Games"));

    /// <summary>Both search paths, for the startup log entry.</summary>
    public static string Describe() => $"local assets='{LocalRoot}', shared='{SharedRoot}'";
}
