using System;
using System.Linq;
using System.Net.NetworkInformation;
using Playfront.App.Input;
using Avalonia.Controls;
using Microsoft.Win32;

namespace Playfront.App.Views;

/// <summary>
/// The "System Console info" screen (Settings -> System -> Console info). Mounted on demand and
/// released on B, like every other screen in the shell.
///
/// The three cards on the left take the ring but do nothing yet. The values on the right are read
/// from the machine at open time - none of them is invented, and any that cannot be read shows
/// "Unavailable" rather than a blank or a placeholder that looks real.
/// </summary>
public partial class SystemConsoleInfoView : UserControl
{
    private const string Unknown = "Unavailable";

    private readonly Border[] _cards;
    private readonly Border[] _rings;
    private int _index;

    /// <summary>Requests closing this screen and returning to Settings (B).</summary>
    public event Action? ExitRequested;

    public SystemConsoleInfoView()
    {
        InitializeComponent();

        _cards = new[] { CiCard0, CiCard1, CiCard2 };
        _rings = new[] { CiRing0, CiRing1, CiRing2 };

        Fill();
        UpdateSelection();
    }

    public void Move(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                ExitRequested?.Invoke();
                return;
            case GamepadButton.Up when _index > 0:
                _index--;
                break;
            case GamepadButton.Down when _index < _cards.Length - 1:
                _index++;
                break;
            default:
                return; // includes A: none of the three does anything yet
        }

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        for (var i = 0; i < _cards.Length; i++)
        {
            var selected = i == _index;
            _cards[i].Classes.Set("selected", selected);
            _rings[i].Classes.Set("selected", selected);
        }
    }

    private void Fill()
    {
        CiDeviceName.Text = Environment.MachineName;
        CiDevice.Text = ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName") ?? Unknown;
        // The closest thing a PC has to a console id: the GUID Windows stamps on the install.
        CiConsoleId.Text = ReadRegistry(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid") ?? Unknown;
        CiOsVersion.Text = WindowsVersion();
        CiShellVersion.Text = PlayfrontVersion.Current;
        CiNetworkId.Text = MacAddress() ?? Unknown;
        CiCopyright.Text = "© 2026 Playfront. All rights reserved.";
    }

    private static string? ReadRegistry(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            var value = key?.GetValue(name) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            // Locked-down machine or a key that moved: the row just says it is unavailable.
            return null;
        }
    }

    // Environment.OSVersion stops at the build and leaves the revision at 0, which is the number that
    // actually changes with every cumulative update. It has to come from the registry: CurrentBuild
    // plus UBR, with the marketing name (25H2) alongside because that is what people recognise.
    private static string WindowsVersion()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null)
            {
                return Unknown;
            }

            var build = key.GetValue("CurrentBuild") as string;
            var ubr = key.GetValue("UBR");
            var display = key.GetValue("DisplayVersion") as string;

            var version = $"10.0.{build}.{ubr}";
            return string.IsNullOrWhiteSpace(display) ? version : $"{version} ({display})";
        }
        catch
        {
            return Unknown;
        }
    }

    // MAC of the interface that is actually carrying traffic. Loopback and tunnels are skipped, and so
    // are the virtual adapters that streaming and remote-desktop tools install, which would otherwise
    // win the race and show an id that has nothing to do with this machine's network card.
    private static string? MacAddress()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .OrderByDescending(n => n.GetIPStatistics().BytesReceived)
                .FirstOrDefault();

            var bytes = nic?.GetPhysicalAddress().GetAddressBytes();
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            return string.Concat(bytes.Select(b => b.ToString("X2")));
        }
        catch
        {
            return null;
        }
    }
}
