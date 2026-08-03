using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Playfront.App.Library;

/// <summary>What kind of thing an entry is. Decides which library category shows it.</summary>
public enum LibraryKind
{
    Game,
    App,
}

/// <summary>
/// How far along an entry is. OWNED means the user added it to the library but it is not on the
/// machine yet - the console's "Get". INSTALLED means it is ready to launch.
/// </summary>
public enum LibraryState
{
    Owned,
    Installed,
}

/// <summary>Where an entry came from. The SOURCE decides the category, never a guess on the name.</summary>
public enum LibrarySource
{
    /// <summary>Playfront's own catalogue (web apps like YouTube). Written by us, so we know it exactly.</summary>
    Playfront,
    /// <summary>Read from an installed Steam library.</summary>
    Steam,
    /// <summary>Read from the list of applications Windows keeps.</summary>
    Windows,
}

/// <summary>One thing in the user's library.</summary>
public sealed class LibraryEntry
{
    /// <summary>Stable identifier, unique across sources ("playfront:youtube", "steam:440"...).</summary>
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";
    public LibraryKind Kind { get; init; }
    public LibrarySource Source { get; init; }
    public LibraryState State { get; set; }

    /// <summary>Artwork file name inside Assets/Icons/Store, or empty when there is none.</summary>
    public string Art { get; init; } = "";

    /// <summary>
    /// Whether going from OWNED to INSTALLED actually has to fetch anything. False for Playfront's
    /// web apps: there is no download, the install step just marks them ready. Kept explicit so the
    /// UI never shows a progress bar for something that does not download - see the note in
    /// LibraryCatalog about YouTube.
    /// </summary>
    public bool NeedsDownload { get; init; }

    /// <summary>
    /// Roughly what this costs on disk once it has been used, in bytes. Shown as "Approximate size"
    /// BEFORE it is installed; afterwards the real folder is measured instead.
    ///
    /// A web app is not free just because Playfront fetches no installer: the embedded browser pulls
    /// down its own pieces on first run (Widevine for protected video, the component cache, the
    /// subresource filter) and then keeps a profile that grows. Measured for YouTube: ~60 MB of
    /// those fixed pieces, and 118 MB total after real use.
    /// </summary>
    public long ApproxBytes { get; init; }

    /// <summary>
    /// Folder under %LocalAppData%\Playfront where this product's data lives, or empty when it keeps
    /// none. What makes the size honest: once it exists, it is measured rather than estimated.
    /// </summary>
    public string DataFolder { get; init; } = "";

    public DateTime AddedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this was last installed, updated or opened. Drives the recents row on Home, newest first.
    /// Settable rather than init-only because it changes every time the product is used.
    ///
    /// Files written before this field existed have no value for it, so it falls back to AddedUtc:
    /// an older library still sorts sensibly instead of every entry claiming the same instant.
    /// </summary>
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The user's library: what they have added ("Get") and what is installed, persisted so it survives
/// restarts. This is the piece the console gets for free and Windows does not - on a console every
/// installed thing already carries its category, here nothing does, so Playfront keeps the list.
///
/// Entries that come from an external source (Steam, Windows) will be RESCANNED rather than stored,
/// because their real state lives elsewhere; what this file holds is what the user chose to have in
/// their library, which is a decision only Playfront knows about.
///
/// Format: one entry per line, fields separated by TAB. Deliberately not JSON: a corrupt or
/// half-written line can be skipped without losing the rest of the file, which matters because this
/// is written on a machine that may be a shell and can lose power at any moment.
/// </summary>
public static class LibraryStore
{
    private static string Path_ => AppData.File("library.tsv");

    private static List<LibraryEntry>? _cache;

    /// <summary>Everything in the library, in the order it was added.</summary>
    public static IReadOnlyList<LibraryEntry> All()
    {
        _cache ??= Load();
        return _cache;
    }

    public static IEnumerable<LibraryEntry> OfKind(LibraryKind kind)
        => All().Where(e => e.Kind == kind);

    public static LibraryEntry? Find(string id)
        => All().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool Contains(string id) => Find(id) is not null;

    /// <summary>Adds an entry if it is not there yet ("Get"). Returns false when it already was.</summary>
    public static bool Add(LibraryEntry entry)
    {
        _cache ??= Load();
        if (Contains(entry.Id))
        {
            return false;
        }

        _cache.Add(entry);
        Save();
        return true;
    }

    /// <summary>Takes an entry out of the library so it stops showing up.</summary>
    public static bool Remove(string id)
    {
        _cache ??= Load();
        var idx = _cache.FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return false;
        }

        _cache.RemoveAt(idx);
        Save();
        return true;
    }

    public static void SetState(string id, LibraryState state)
    {
        var entry = Find(id);
        if (entry is null || entry.State == state)
        {
            return;
        }

        entry.State = state;

        // Installing counts as using it: it is what the recents row on Home is for, and a product
        // that was just installed is the most recent thing that happened.
        if (state == LibraryState.Installed)
        {
            entry.LastUsedUtc = DateTime.UtcNow;
        }

        Save();
    }

    /// <summary>
    /// Stamps a product as used right now, which is what puts it at the head of the recents row.
    /// Called when it is opened; installing does it through <see cref="SetState"/>.
    /// </summary>
    public static void Touch(string id)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return;
        }

        entry.LastUsedUtc = DateTime.UtcNow;
        Save();
    }

    /// <summary>
    /// The library newest-first: what was last installed, updated or opened comes first. Ties are
    /// broken by when it was added, so a fresh library still has a stable order instead of shuffling.
    /// </summary>
    public static IEnumerable<LibraryEntry> Recent()
        => All().OrderByDescending(e => e.LastUsedUtc).ThenByDescending(e => e.AddedUtc);

    private static List<LibraryEntry> Load()
    {
        var list = new List<LibraryEntry>();
        try
        {
            if (!File.Exists(Path_))
            {
                return list;
            }

            foreach (var line in File.ReadAllLines(Path_))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var f = line.Split('\t');
                // A line written half-way (power cut) is skipped instead of taking the file down.
                if (f.Length < 7)
                {
                    continue;
                }

                if (!Enum.TryParse<LibraryKind>(f[2], out var kind)) continue;
                if (!Enum.TryParse<LibrarySource>(f[3], out var source)) continue;
                if (!Enum.TryParse<LibraryState>(f[4], out var state)) continue;

                list.Add(new LibraryEntry
                {
                    Id = f[0],
                    Title = f[1],
                    Kind = kind,
                    Source = source,
                    State = state,
                    Art = f[5],
                    NeedsDownload = f[6] == "1",
                    AddedUtc = f.Length > 7 && DateTime.TryParse(f[7], CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.UtcNow,
                    LastUsedUtc = f.Length > 8 && DateTime.TryParse(f[8], CultureInfo.InvariantCulture,
                                      DateTimeStyles.RoundtripKind, out var u)
                        ? u
                        // Written before this column existed: fall back to when it was added.
                        : f.Length > 7 && DateTime.TryParse(f[7], CultureInfo.InvariantCulture,
                              DateTimeStyles.RoundtripKind, out var d2) ? d2 : DateTime.UtcNow,
                });
            }
        }
        catch (Exception e)
        {
            CrashLog.Info($"LibraryStore: could not read {Path_}: {e.Message}");
        }

        return list;
    }

    private static void Save()
    {
        if (_cache is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppData.Folder);
            var lines = _cache.Select(e => string.Join('\t',
                e.Id, e.Title, e.Kind, e.Source, e.State, e.Art,
                e.NeedsDownload ? "1" : "0",
                e.AddedUtc.ToString("O", CultureInfo.InvariantCulture),
                e.LastUsedUtc.ToString("O", CultureInfo.InvariantCulture)));

            // Write to a temp file and swap: a power cut mid-write leaves the previous library
            // intact instead of a truncated one.
            var tmp = Path_ + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Copy(tmp, Path_, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception e)
        {
            CrashLog.Info($"LibraryStore: could not write {Path_}: {e.Message}");
        }
    }
}
