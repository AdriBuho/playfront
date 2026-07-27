using System;
using System.Diagnostics;
using System.Reflection;

namespace Playfront.App;

/// <summary>
/// Playfront's version, for display and for the app log. No number is hard-coded here on purpose: it
/// is read back from the executable, where the build stamps it from PlayfrontVersion in
/// Directory.Build.props — the single place it is defined. That makes it impossible for the version
/// shown in the app to drift from the .exe or the installer.
/// </summary>
internal static class PlayfrontVersion
{
    // Computed once: reading executable metadata is cheap but not free, and the UI asks for this.
    private static readonly Lazy<string> Lazy = new(Read);

    /// <summary>Short version, as shown to the user. For example "0.1.0".</summary>
    public static string Current => Lazy.Value;

    /// <summary>What the UI displays. For example "Playfront 0.1.0".</summary>
    public static string Display => "Playfront " + Current;

    private static string Read()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();

            // InformationalVersion is the one that mirrors <Version> verbatim: AssemblyVersion always
            // appends an extra ".0", and FileVersion rejects suffixes such as "-beta".
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Some build tooling appends "+<commit id>", which means nothing to the user.
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            // Fallbacks if that attribute is missing: file properties, then assembly version.
            var file = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
            if (!string.IsNullOrWhiteSpace(file)) return file;

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            // Not knowing the version must never stop the app from starting.
            return "unknown";
        }
    }
}
