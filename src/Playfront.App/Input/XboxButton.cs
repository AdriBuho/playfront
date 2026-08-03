using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;

namespace Playfront.App.Input;

/// <summary>
/// Takes the Xbox (Guide) button away from Windows' Game Bar while the shell is running.
///
/// Left alone, that button opens the Xbox overlay ON TOP of Playfront - a second launcher over the
/// first, with no way back here from it. The button is still read the usual way (see GamepadPoller,
/// which needs an undocumented entry point to see it at all), so it reaches GoHome exactly as
/// before; this only stops Windows from reacting to it as well.
///
/// The switch is one per-user registry value. No administrator rights and no restart: Game Bar
/// reads it live, verified by pressing the button before and after. Win+G is untouched - the value
/// only covers the button on a pad. It is not specific to any device either: it fires on any PC
/// from any XInput pad, virtual ones included.
///
/// PUT BACK ON EXIT, deliberately. With Playfront closed the button has to do what Windows means it
/// to, or the machine just looks broken. The previous value is written to disk BEFORE anything is
/// changed, so a process that gets killed is still repaired on the next launch - the same safety
/// net, and for the same reason, as SystemCursor.
///
/// NOT COVERED: holding the button still opens Windows' Task View. That is a separate binding and
/// there is no registry value for it - HKCU holds exactly one "Nexus" value and this is it.
/// </summary>
internal static class XboxButton
{
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string NexusValue = "UseNexusForGameBarEnabled";

    // Backup and marker in one file: what the value was before we touched it, or "absent" when there
    // was none. Its mere presence is what says a restore is still owed.
    private const string Absent = "absent";

    private static string BackupPath => AppData.File("xbox-button.txt");

    /// <summary>Sends the Xbox button here instead of to the Game Bar.</summary>
    public static void Divert()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GameBarKey);
            if (key is null)
            {
                return;
            }

            // Never overwrite an existing backup: a second Divert with no Restore in between would
            // record OUR value as the user's, and the real one would be lost for good.
            if (!File.Exists(BackupPath))
            {
                var current = key.GetValue(NexusValue);
                Save(current is int n ? n.ToString(CultureInfo.InvariantCulture) : Absent);
            }

            key.SetValue(NexusValue, 0, RegistryValueKind.DWord);
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not take the Xbox button off the Game Bar", e);
        }
    }

    /// <summary>
    /// Gives the button back to Windows. Does nothing when nothing is owed, so it is safe to call on
    /// every exit and again on every launch.
    /// </summary>
    public static void Restore()
    {
        try
        {
            if (!File.Exists(BackupPath))
            {
                return;
            }

            var saved = File.ReadAllText(BackupPath).Trim();

            using (var key = Registry.CurrentUser.CreateSubKey(GameBarKey))
            {
                if (key is not null)
                {
                    if (saved == Absent)
                    {
                        // There was no value at all before us. Writing a 1 instead would not be the
                        // same thing: it would leave a setting behind that the user never set.
                        key.DeleteValue(NexusValue, throwOnMissingValue: false);
                    }
                    else if (int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        key.SetValue(NexusValue, n, RegistryValueKind.DWord);
                    }
                }
            }

            // Last, so a failure above leaves the backup in place and the next launch tries again.
            File.Delete(BackupPath);
        }
        catch (Exception e)
        {
            CrashLog.Log("Could not give the Xbox button back to the Game Bar", e);
        }
    }

    private static void Save(string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, value);
    }
}
