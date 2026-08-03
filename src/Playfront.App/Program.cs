using Avalonia;
using System;
using System.Runtime.InteropServices;
using Velopack;

namespace Playfront.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must be the FIRST line of Main. Right after installing or updating, the installer
        // relaunches this same .exe with special arguments to create the shortcut, clean up the
        // previous version and so on. This call services those requests and ends the process without
        // opening a window. Starting the UI first would flash Playfront on screen and close it again
        // on every install and every update. On a normal launch it does nothing and falls through.
        VelopackApp.Build().Run();

        // Before anything else: move user data from the era when the project was called Atlas
        // (%LocalAppData%\Atlas -> ...\Playfront). It has to run here because several classes resolve
        // their path once, on first use — moving it later would have no effect.
        AppData.MigrateLegacyFolder();

        // Hidden poster-capture mode: decodes a video's first frame with the SAME decoder that plays it
        // (Media Foundation), writes it raw and exits without starting the UI. Used to generate posters
        // whose colours match the video (see Video/PosterCapture.cs). Format: "videoPath|rawOutputPath".
        var capture = Environment.GetEnvironmentVariable("PLAYFRONT_CAPTURE_POSTER");
        if (!string.IsNullOrEmpty(capture))
        {
            var parts = capture.Split('|');
            var (w, h) = Playfront.App.Video.PosterCapture.CaptureFirstFrameRaw(parts[0], parts[1]);
            Console.WriteLine($"CAPTURED {w}x{h} -> {parts[1]}");
            return;
        }

        // Hooks the global error log as early as possible and guards startup and the main loop. If
        // something fatal takes the app down, the reason lands in %LocalAppData%\Playfront\playfront.log
        // and the process dies; relaunching is the external watchdog's job, not this one's.
        CrashLog.Install();

        // First line of every startup: which version, on which Windows. This is the base of the
        // diagnostic report — when someone reports a problem, this already says which version it
        // happened on without having to ask.
        CrashLog.Info($"Playfront {PlayfrontVersion.Current} starting on Windows {Environment.OSVersion.Version} ({RuntimeInformation.ProcessArchitecture})");

        // Without the heavy assets the app works but looks empty. Logged so that "everything is grey"
        // can be diagnosed without guessing.
        if (!AssetPaths.HeavyAssetsAvailable)
        {
            CrashLog.Info($"Heavy assets NOT found: the UI will have no backgrounds or artwork. Searched {AssetPaths.Describe()}.");
        }

        // SAFETY NET for the system-wide mouse pointer. Playfront replaces it while driving an
        // outside program, and that change survives the process: a crash would leave the user with
        // our pointer on every application until they signed out.
        //
        // Restoring here, unconditionally, is what actually protects them - the tidy-up paths inside
        // the app cannot run if the process is killed, but this one runs on the next launch. It costs
        // nothing when there is nothing to repair, since it just tells Windows to reload the user's
        // own cursor scheme.
        Input.SystemCursor.Restore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Input.SystemCursor.Restore();

        // The Xbox button, same shape and for the same reason: Windows would otherwise open its own
        // overlay on top of the shell. Restore FIRST - that repairs a previous run that was killed
        // before it could tidy up - and only then take the button over. Handing it back on exit is
        // the point: with Playfront closed it has to behave the way Windows means it to.
        Input.XboxButton.Restore();
        Input.XboxButton.Divert();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Input.XboxButton.Restore();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            CrashLog.Log("FATAL (Main)", e);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // AngleEgl renders through Direct3D 11 instead of the default backend. Required for the
            // hardware video background to hand frames to Avalonia without going through system memory
            // (see Video/HardwareVideoBackgroundControl.cs).
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl] })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
