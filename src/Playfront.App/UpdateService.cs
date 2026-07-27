using System;
using System.IO;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Playfront.App;

/// <summary>
/// Check for a new version, download it, and restart into it. Logic only — it draws nothing, so the
/// screen that uses it owns the whole appearance.
///
/// Backed by Velopack (MIT). Two consequences worth knowing:
///  - It installs under the user's profile, so updating never prompts for administrator rights.
///  - It only works when the app was installed by its installer. Running from a build output folder
///    leaves nothing to update: that is <see cref="UpdateState.Unsupported"/>, not an error, and the
///    UI is expected to say so rather than stay silent.
/// </summary>
internal enum UpdateState
{
    /// <summary>Not checked yet.</summary>
    Idle,

    /// <summary>Asking whether a newer version exists.</summary>
    Checking,

    /// <summary>Can't update from here: not installed by the installer.</summary>
    Unsupported,

    /// <summary>Checked: already on the latest version.</summary>
    UpToDate,

    /// <summary>A newer version exists, not downloaded yet.</summary>
    Available,

    /// <summary>Downloading (see <see cref="UpdateService.Progress"/>).</summary>
    Downloading,

    /// <summary>Downloaded and staged: restarting applies it.</summary>
    ReadyToRestart,

    /// <summary>Something failed (no network, source unreachable, disk full...). See <see cref="UpdateService.LastError"/>.</summary>
    Failed,
}

internal sealed class UpdateService
{
    /// <summary>
    /// Where updates come from. GitHub Releases has no download or size caps and is free for public
    /// repositories.
    ///
    /// This must match the real repository exactly. A mismatch produces no visible error: the check
    /// simply finds nothing and the app stays quiet, believing it is up to date. Update this if the
    /// repository is ever renamed or moved.
    /// </summary>
    private const string GithubRepository = "https://github.com/AdriBuho/playfront";

    /// <summary>
    /// Testing escape hatch: when set, updates are looked up there (a local folder or a URL) instead
    /// of GitHub. Keeps test-only wiring out of the product.
    /// </summary>
    private const string SourceOverrideVariable = "PLAYFRONT_UPDATE_SOURCE";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public UpdateService()
    {
        // Building the UpdateManager touches no network; it only inspects how the app was installed.
        // It can still throw (half-finished install, odd permissions), and that must not stop the app
        // from starting: it just ends up without updates and says so.
        try
        {
            var over = Environment.GetEnvironmentVariable(SourceOverrideVariable);
            IUpdateSource source = string.IsNullOrWhiteSpace(over)
                ? new GithubSource(GithubRepository, accessToken: null, prerelease: false)
                : new SimpleFileSource(new DirectoryInfo(over));

            _manager = new UpdateManager(source);
        }
        catch (Exception e)
        {
            CrashLog.Log("UpdateService (init)", e);
            _manager = null;
        }
    }

    /// <summary>Where the process currently is. The UI renders from this.</summary>
    public UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>Download progress, 0-100. Only meaningful while <see cref="UpdateState.Downloading"/>.</summary>
    public int Progress { get; private set; }

    /// <summary>The version that would be installed, if any. For example "0.1.1".</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Why it failed, phrased for the user.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised on every state or progress change, so the UI can repaint.</summary>
    public event Action? Changed;

    /// <summary>
    /// Installed version according to Velopack. Differs from <see cref="PlayfrontVersion.Current"/>
    /// when the app was not installed by the installer, where this returns null.
    /// </summary>
    public string? InstalledVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>Asks whether a newer version exists.</summary>
    public async Task CheckAsync()
    {
        if (_manager is null || !_manager.IsInstalled)
        {
            // Routine during development (dotnet run) and for a hand-copied build folder.
            LastError = "Updates are only available when Playfront is installed with its installer.";
            Set(UpdateState.Unsupported);
            return;
        }

        Set(UpdateState.Checking);
        try
        {
            _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pending is null)
            {
                AvailableVersion = null;
                Set(UpdateState.UpToDate);
                CrashLog.Info($"Updates: already on the latest version ({InstalledVersion}).");
                return;
            }

            AvailableVersion = _pending.TargetFullRelease.Version.ToString();
            Set(UpdateState.Available);
            CrashLog.Info($"Updates: {AvailableVersion} available (installed {InstalledVersion}).");
        }
        catch (Exception e)
        {
            Fail("Couldn't check for updates. Check your internet connection.", e, "UpdateService (check)");
        }
    }

    /// <summary>Downloads the new version. The app stays usable meanwhile.</summary>
    public async Task DownloadAsync()
    {
        if (_manager is null || _pending is null) return;

        Progress = 0;
        Set(UpdateState.Downloading);
        try
        {
            await _manager.DownloadUpdatesAsync(_pending, p =>
            {
                Progress = p;
                Changed?.Invoke();
            }).ConfigureAwait(false);

            Set(UpdateState.ReadyToRestart);
            CrashLog.Info($"Updates: {AvailableVersion} downloaded, ready to apply on restart.");
        }
        catch (Exception e)
        {
            Fail("Couldn't download the update.", e, "UpdateService (download)");
        }
    }

    /// <summary>
    /// Applies the staged update and restarts. Does not return: the process dies here.
    /// Once Playfront is the Windows shell, nothing is on screen during that restart.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _pending is null || State != UpdateState.ReadyToRestart) return;

        try
        {
            CrashLog.Info($"Updates: applying {AvailableVersion} and restarting.");
            _manager.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception e)
        {
            Fail("Couldn't apply the update.", e, "UpdateService (apply)");
        }
    }

    private void Fail(string userMessage, Exception e, string context)
    {
        CrashLog.Log(context, e);
        LastError = userMessage;
        Set(UpdateState.Failed);
    }

    private void Set(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed && state != UpdateState.Unsupported) LastError = null;
        Changed?.Invoke();
    }
}
