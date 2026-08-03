using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playfront.App.Library;

/// <summary>
/// Playfront's own catalogue: the things the Store can offer that Playfront itself provides, as
/// opposed to what it merely finds on the machine (Steam, Windows). Keyed by the artwork file name,
/// which is what the Store pages already use to identify a product.
///
/// YouTube is the first one, and it is worth being clear about what "install" means for it: it is a
/// WEB app (youtube.com/tv in an embedded browser), so Playfront fetches no installer. NeedsDownload
/// is false and the install step just marks it ready; no progress bar, because showing one would be
/// inventing work that does not happen.
///
/// That is NOT the same as costing nothing on disk, and the difference bit once. The embedded browser
/// pulls its own pieces down the first time the app runs and then keeps a profile. Measured on a real
/// machine: 118 MB, of which ~60 MB are fixed components (Widevine 21.6, component cache 21.2,
/// subresource filter 11.5, shader caches 6.6) and the rest is the profile, which grows with use.
/// So ApproxBytes is a real figure and the Store measures the folder once it exists.
/// </summary>
public static class LibraryCatalog
{
    private static readonly Dictionary<string, LibraryEntry> ByArt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube.png"] = new LibraryEntry
        {
            Id = "playfront:youtube",
            Title = "YouTube",
            Kind = LibraryKind.App,
            Source = LibrarySource.Playfront,
            Art = "youtube.png",
            NeedsDownload = false,
            // The fixed pieces the browser pulls on first run. The profile on top of this is the
            // user's own data and is not part of what the product "costs" to add.
            ApproxBytes = 60L * 1024 * 1024,
            DataFolder = "YouTube",
        },
        // Spotify. NOT a web app like YouTube: this installs the real Windows application.
        //
        // Spotify publishes no TV build on the web - open.spotify.com/tv is a 404, tv.spotify.com
        // does not resolve, and a TV user-agent gets the same desktop page - so embedding it would
        // only ever be "a web page, bigger". The desktop app measured better on every count that
        // matters here (440 MB against 805 for the same thing in the embedded browser, a third of
        // the CPU), plays at higher quality, works offline, and publishes its track to Windows so
        // anything on the machine can show and control what is playing.
        ["spotify-black.png"] = new LibraryEntry
        {
            Id = "playfront:spotify",
            Title = "Spotify",
            Kind = LibraryKind.App,
            Source = LibrarySource.Playfront,
            Art = "spotify-black.png",
            NeedsDownload = true, // a real ~150 MB download, unlike the web apps
            ApproxBytes = 398L * 1024 * 1024, // measured on disk after installing
            DataFolder = "", // it keeps its data in its own folders, not under Playfront's
        },
        // Steam. Installed by the helper service rather than winget - see ExternalApps for why - and
        // no ApproxBytes: a fresh client has never been measured here, and the folder on this machine
        // holds the user's games, so it cannot be measured now either. Blank beats a guess.
        ["steam.png"] = new LibraryEntry
        {
            Id = "playfront:steam",
            Title = "Steam",
            Kind = LibraryKind.App,
            Source = LibrarySource.Playfront,
            Art = "steam.png",
            NeedsDownload = true,
            DataFolder = "", // it keeps its data in its own folders, not under Playfront's
        },
    };

    /// <summary>
    /// A product Playfront offers but does not build: something installed for the user and launched as
    /// a normal Windows program. Kept OUT of <see cref="LibraryEntry"/> on purpose - this is
    /// catalogue data, not user data, so it never goes into the saved library file and the file's
    /// format does not change.
    /// </summary>
    /// <param name="ExeTemplate">Where it lands by default, with %APPDATA% and friends still in it.</param>
    /// <param name="WingetId">Installed through winget when set.</param>
    /// <param name="HelperCommand">
    /// Installed by the helper service instead, when set. Needed by anything that writes outside the
    /// user's own folders: winget would raise a UAC prompt, and the helper is already SYSTEM.
    /// </param>
    /// <param name="InstallPathKey">
    /// Registry key holding an "InstallPath" value that overrides the folder in ExeTemplate. The
    /// filename is kept. Without it, a product the user installed somewhere else would read as
    /// missing.
    /// </param>
    /// <param name="ControllerNative">
    /// True for a program that already drives itself with a controller and already owns the whole
    /// screen. Playfront then hands it the display and stays out of the way: no pointer, no cursor
    /// swap, no cover over its window buttons.
    ///
    /// Steam launched with -gamepadui is the case. Treating it like Spotify put a mouse on top of an
    /// interface built for a pad, and dropped the button cover over Steam's own clock - it has no
    /// window buttons to hide in the first place.
    /// </param>
    /// <param name="WindowProcess">
    /// The process that owns the window on screen, when it is not the one that was launched. Steam
    /// needs it: started with -gamepadui the visible Big Picture window belongs to "steamwebhelper",
    /// while "steam.exe" is only the launcher behind it. Looking for the window under the wrong name
    /// finds nothing, and then nothing gets brought to the front or made fullscreen.
    /// </param>
    /// <param name="Arguments">
    /// Command line handed to the program when Playfront runs it. Steam gets "-gamepadui", which
    /// starts it straight in Big Picture - its controller-first interface, and the only sensible one
    /// on a screen driven by a pad. Verified on this machine: it comes up full screen with the A/B
    /// hints, not the desktop window.
    /// </param>
    /// <param name="ExcludeFolders">
    /// Subfolders left out when measuring the size, so the figure is comparable between machines.
    /// Steam needs two: "steamapps" holds the user's GAMES, and "package" holds update packages it
    /// has downloaded over time - 305 MB on the machine this was measured on, against a nearly empty
    /// folder on a fresh install. Neither answers "how much does Steam weigh".
    /// </param>
    public sealed record ExternalApp(
        string ExeTemplate,
        string? WingetId = null,
        string? HelperCommand = null,
        string? InstallPathKey = null,
        string[]? ExcludeFolders = null,
        string? Arguments = null,
        string? WindowProcess = null,
        bool ControllerNative = false)
    {
        /// <summary>Whose window to look for on screen. The launched program itself unless it says otherwise.</summary>
        public string WindowProcessName =>
            WindowProcess ?? Path.GetFileNameWithoutExtension(ExeTemplate);

        /// <summary>Where the program actually is: the registry when it says, the default otherwise.</summary>
        public string ExePath
        {
            get
            {
                var porDefecto = Environment.ExpandEnvironmentVariables(ExeTemplate);
                if (InstallPathKey is null)
                {
                    return porDefecto;
                }

                var raiz = Microsoft.Win32.Registry.GetValue(InstallPathKey, "InstallPath", null) as string;
                return string.IsNullOrWhiteSpace(raiz)
                    ? porDefecto
                    : Path.Combine(raiz, Path.GetFileName(porDefecto));
            }
        }

        /// <summary>
        /// The SYSTEM is the source of truth for "is it installed", not Playfront's own library file.
        /// Checking the file cannot drift: uninstalling from Windows is noticed straight away.
        /// </summary>
        public bool IsInstalled => File.Exists(ExePath);
    }

    private static readonly Dictionary<string, ExternalApp> ExternalApps = new(StringComparer.OrdinalIgnoreCase)
    {
        // Spotify installs per-user, which is why no administrator rights are needed - and why its
        // installer REFUSES to run elevated.
        ["playfront:spotify"] = new ExternalApp(@"%APPDATA%\Spotify\Spotify.exe", WingetId: "Spotify.Spotify"),

        // Steam goes through the helper, not winget: it installs into Program Files, so winget would
        // put a UAC prompt in front of a user holding a controller. The helper is already SYSTEM and
        // checks the installer is signed by Valve before running it.
        ["playfront:steam"] = new ExternalApp(
            @"%ProgramFiles(x86)%\Steam\steam.exe",
            HelperCommand: "install-steam",
            InstallPathKey: @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            ExcludeFolders: new[] { "steamapps", "package" },
            Arguments: "-gamepadui",
            WindowProcess: "steamwebhelper",
            ControllerNative: true),
    };

    /// <summary>The external-app details for a library entry, or null when it is one of ours.</summary>
    public static ExternalApp? ExternalFor(string? id)
        => id is not null && ExternalApps.TryGetValue(id, out var a) ? a : null;

    /// <summary>
    /// What this product occupies right now, or null when it has not been run yet. Measured, not
    /// estimated: it walks the product's own data folder.
    /// </summary>
    public static long? SizeOnDisk(LibraryEntry entry)
    {
        // An external app lives in its own install folder, not under Playfront's data.
        var external = ExternalFor(entry.Id);

        var root = external is not null
            ? Path.GetDirectoryName(external.ExePath)
            : string.IsNullOrEmpty(entry.DataFolder) ? null : Path.Combine(AppData.Folder, entry.DataFolder);

        if (root is null) return null;

        try
        {
            if (!Directory.Exists(root)) return null;

            // Subfolders skipped so the figure answers "how big is this product" and not "how big is
            // what you put in it" or "how long have you had it".
            var saltar = (external?.ExcludeFolders ?? Array.Empty<string>())
                .Select(s => Path.Combine(root, s) + Path.DirectorySeparatorChar)
                .ToArray();

            long total = 0;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (saltar.Any(s => f.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // A file can vanish mid-walk (the browser rotates its caches while running); one
                // unreadable file must not lose the whole figure.
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total > 0 ? total : null;
        }
        catch
        {
            return null; // no permission or the folder went away: fall back to the estimate
        }
    }

    /// <summary>The catalogue entry for a Store product, or null when Playfront does not provide it.</summary>
    public static LibraryEntry? ForArt(string? art)
        => art is not null && ByArt.TryGetValue(art, out var e) ? e : null;

    /// <summary>A fresh copy of the entry, so what goes into the library is not the template itself.</summary>
    public static LibraryEntry? NewEntryForArt(string? art)
    {
        var t = ForArt(art);
        return t is null ? null : new LibraryEntry
        {
            Id = t.Id,
            Title = t.Title,
            Kind = t.Kind,
            Source = t.Source,
            Art = t.Art,
            NeedsDownload = t.NeedsDownload,
            State = LibraryState.Owned,
            AddedUtc = DateTime.UtcNow,
        };
    }
}
