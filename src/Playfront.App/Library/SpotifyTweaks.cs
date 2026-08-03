using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Playfront.App.Library;

/// <summary>
/// The settings Playfront applies to Spotify right after installing it. Both were measured on this
/// hardware rather than taken from advice found online, and both are the SAME settings Spotify's own
/// options screen writes - so its UI shows them switched off, which is the truth. Nothing is changed
/// behind the app's back.
///
/// 1. HARDWARE ACCELERATION OFF. Measured with music playing, same track, 14 samples per state:
///    video memory 261 MB -> 0, private RAM 1094 -> 710 MB, GPU time 7.75% -> 1.07%, CPU +1.3 points.
///    With the window HIDDEN - the case that matters, a game in front - the CPU cost falls to +0.03
///    points while the memory saving stays. On a handheld with unified memory that video memory is
///    memory taken from the game, and it is NOT released by minimising (measured: 0.5 MB).
///
///    Do NOT use "disable_accelerated_drawing", which is what most guides say. It does nothing: with
///    that line set the GPU process still starts, with an identical --gpu-preferences string and the
///    same memory.
///
/// 2. AUTOSTART OFF. Spotify does two things when this is switched off in its own settings, and both
///    are needed. Removing only the registry value leaves the preference absent, and absent means ON,
///    so its settings screen would still claim it starts with Windows and it would likely put the
///    registry value back on the next launch.
///
/// Canvas (the looping videos behind album art) is NOT here on purpose: it lives in the embedded
/// browser's Local Storage database keyed by the user's account id, so it does not exist until
/// someone signs in and it cannot be written safely from outside.
/// </summary>
public static class SpotifyTweaks
{
    // None of these lines exists by default - both options ship enabled - so they are ADDED.
    private const string AccelLine = "ui.hardware_acceleration=false";
    private const string AutostartLine = "app.autostart-mode=\"off\"";

    // Load-bearing, and it cost a test run to find. On its FIRST launch Spotify runs a "configure
    // autostart" routine, and it decides whether to run it by looking at this flag. Without it, that
    // routine wipes our app.autostart-mode line and registers Spotify in Windows startup anyway.
    // Writing it up front makes Spotify believe the question is already settled, so it leaves our
    // answer alone.
    private const string AutostartDoneLine = "app.autostart-configured=true";

    private static string PrefsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "prefs");

    /// <summary>
    /// Applies both settings. Spotify must be CLOSED: it rewrites this file when it exits and would
    /// undo the change.
    ///
    /// Called right after installing, when the file does NOT exist yet - Spotify creates it on its
    /// FIRST RUN, not when the installer finishes. So this writes the file itself. That turns out to
    /// be better than fixing things afterwards: Spotify reads our two lines on its very first
    /// launch, so it never turns hardware acceleration on and never adds itself to Windows startup
    /// in the first place.
    /// </summary>
    public static bool Apply()
    {
        var ok = WritePrefs();
        RemoveAutostartEntry();
        return ok;
    }

    private static bool WritePrefs()
    {
        try
        {
            var path = PrefsPath;
            var dir = Path.GetDirectoryName(path);
            if (dir is null)
            {
                return false;
            }

            if (!Directory.Exists(dir))
            {
                return false; // Spotify is not installed: nothing to configure
            }

            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            var changed = false;

            foreach (var (key, line) in new[]
                     {
                         ("ui.hardware_acceleration", AccelLine),
                         ("app.autostart-configured", AutostartDoneLine),
                         ("app.autostart-mode", AutostartLine),
                     })
            {
                var i = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
                if (i >= 0)
                {
                    if (lines[i] == line) continue;
                    lines[i] = line;
                }
                else
                {
                    lines.Add(line);
                }

                changed = true;
            }

            if (changed)
            {
                File.WriteAllLines(path, lines);
            }

            return true;
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not write Spotify preferences", e);
            return false;
        }
    }

    // HKCU, so no administrator rights are needed - which matters, because installing happens from
    // the app and the app deliberately runs without them.
    private static void RemoveAutostartEntry()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

            if (run?.GetValue("Spotify") is not null)
            {
                run.DeleteValue("Spotify", throwOnMissingValue: false);
            }
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not remove Spotify's autostart entry", e);
        }
    }
}
