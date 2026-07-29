using System;
using System.IO;

namespace Playfront.App;

// Home background selection. Three possibilities: the default dynamic video, one of the 14 solid
// colours from the "Solid colors" picker, or a specific video chosen in "Dynamic backgrounds". The
// choice is persisted and reloaded at startup, like AccentTheme does for the accent colour. It uses
// its own file (background.txt) because the two are independent settings: the theme colour (borders,
// selection) and the wallpaper need not match.
public static class BackgroundSettings
{
    // The 13 solid colours, sampled pixel by pixel from the centre of each swatch in the reference
    // (flat fills, no gradient: centre and corners read identical).
    //
    // These occupy GRID SLOTS 1..13, not 0..12: slot 0 of the 7x2 grid is the custom colour picker
    // (the hue-wheel tile), which is not a solid colour. Callers convert with SolidHexAtSlot /
    // SolidSlotOf in MainWindow.
    public static readonly string[] SolidPalette =
    {
        /* slots 1..6  */ "#3A3A3A", "#126E10", "#106D6F", "#125DA7", "#603499", "#B9335F",
        /* slots 7..13 */ "#101010", "#399618", "#107565", "#0F72B4", "#15357B", "#862B92", "#95115C",
    };

    // Stored value: "#RRGGBB" means that solid colour; "video:<path relative to Assets/Backgrounds>"
    // means that specific video; anything else (or nothing) means the default dynamic video.
    private static string SettingsPath => AppData.File("background.txt");

    // Hex of the stored solid colour, or null when the background is a video (default, or when the
    // file can't be read).
    public static string? LoadSolidHex()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var value = File.ReadAllText(SettingsPath).Trim();
                if (value.StartsWith("#", StringComparison.Ordinal))
                {
                    return value;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable: fall back to the dynamic video.
        }

        return null;
    }

    // Path (relative to Assets/Backgrounds) of the chosen video, or null when the background is not a
    // specific video (solid colour, or the default dynamic one).
    public static string? LoadVideoRelPath()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var value = File.ReadAllText(SettingsPath).Trim();
                if (value.StartsWith("video:", StringComparison.Ordinal))
                {
                    return value["video:".Length..];
                }
            }
        }
        catch
        {
            // Unreadable: fall back to the default dynamic video.
        }

        return null;
    }

    public static void SaveSolid(string hex) => Write(hex);

    public static void SaveVideo(string relPath) => Write("video:" + relPath);

    public static void SaveDynamic() => Write("dynamic");

    private static void Write(string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, value);
        }
        catch
        {
            // Best-effort, like AccentTheme: on failure the background still applies for this session,
            // it just won't survive a restart.
        }
    }
}
