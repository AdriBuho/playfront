using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace Playfront.App.Web;

/// <summary>
/// Hosts a WebView2 browser (the Edge engine, already present on the machine) inside the Playfront UI,
/// for console-style web apps - today the YouTube TV interface (youtube.com/tv).
///
/// This is a <see cref="NativeControlHost"/>: WebView2 is a NATIVE Windows window, not something
/// Avalonia draws. Avalonia creates a child window and the WebView2 controller is mounted on top of it.
/// Known consequence ("airspace"): this view ALWAYS draws above anything Avalonia paints, so the
/// YouTube screen is full-screen with no Playfront chrome over it.
///
/// The controller does not reach the page on its own (the Leanback UI is driven with arrows+Enter, not
/// the Gamepad API): Playfront maps the controller to keys and injects them through
/// <see cref="SendKey"/>, which calls a JavaScript helper pre-injected into the page.
///
/// Lifecycle: <see cref="Suspend"/> when focus is lost (a game came to the foreground) and a full
/// release in <see cref="DestroyNativeControlCore"/> when leaving the screen.
/// </summary>
public sealed class WebViewHost : NativeControlHost
{
    // Console user-agent: makes YouTube serve the TV (Leanback) interface instead of the normal site.
    // Cobalt 19 on purpose - it stops YouTube assuming Widevine DRM, which WebView2 does not carry.
    private const string UserAgent =
        "Mozilla/5.0 (PS4; Leanback Shell) Cobalt/19.lts.0-qa; compatible; Playfront/1.0";

    private readonly string _url;
    private readonly string _profileFolder;

    // Pre-injected JS helper, called as __playfrontKey(code) to synthesize a keypress (keydown+keyup)
    // the Leanback UI understands. This is the reliable route; posting WM_KEYDOWN to the HWND is not.
    //
    // Each event is dispatched ONCE, and only on 'document'. Dispatching on document.activeElement as
    // well duplicates it: Leanback listens on 'document', and the activeElement event bubbles back up
    // to 'document' too, so every press counted TWICE and moved two rows. document.dispatchEvent with
    // bubbles:true already reaches the 'document' and 'window' listeners, which is where Leanback sits.
    private const string InjectScript = @"
        window.__playfrontKey = function (code) {
            ['keydown','keyup'].forEach(function (type) {
                var e = new KeyboardEvent(type, { bubbles: true, cancelable: true, keyCode: code, which: code });
                document.dispatchEvent(e);
            });
        };";

    private CoreWebView2Controller? _controller;
    private IntPtr _hwnd;
    private bool _disposed;

    /// <summary>Raised when the browser cannot initialize (typically the WebView2 runtime is missing).</summary>
    public event Action<string>? InitFailed;

    /// <summary>
    /// A shell key pressed or released while the browser has focus. Second argument is true for a press.
    ///
    /// This exists because WebView2 is a NATIVE window: while it is up it owns the keyboard and the
    /// Avalonia window never sees a KeyDown. Without this, the only way out of this screen was holding
    /// B on a controller, so a PC with no controller had no way back at all.
    /// </summary>
    public event Action<int, bool>? ShellKey;

    // Virtual key codes forwarded to the shell. Deliberately only the "back" keys: everything else the
    // Leanback interface already handles natively (arrows navigate, Enter selects), and search and
    // play/pause are reachable through the page's own UI, so intercepting them would only add a
    // round trip.
    private const int VkBack = 0x08;
    private const int VkEscape = 0x1B;

    public WebViewHost(string url, string profileFolder)
    {
        _url = url;
        _profileFolder = profileFolder;

        // Whenever Avalonia moves or resizes this view, resize the browser to fill it (the whole window
        // in the YouTube case).
        this.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(_ => UpdateBounds()));
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent); // Avalonia creates the child window (HWND)
        _hwnd = handle.Handle;
        _ = InitializeAsync(); // starts browser creation async; continues on the UI thread
        return handle;
    }

    private async Task InitializeAsync()
    {
        try
        {
            // userDataFolder holds cookies and session (persistent login). NEVER wipe it in cleanups:
            // deleting it means signing in again.
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _profileFolder,
                options: null);

            if (_disposed)
            {
                return; // left the screen before the browser finished being created
            }

            _controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            if (_disposed)
            {
                _controller.Close();
                _controller = null;
                return;
            }

            var core = _controller.CoreWebView2;

            // UA BEFORE navigating - it applies to every request the control makes.
            core.Settings.UserAgent = UserAgent;

            // Kiosk settings: no menus, browser accelerator keys, devtools, zoom or bars. This is a
            // full-screen controller-driven app, not a browser.
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsSwipeNavigationEnabled = false;

            // YouTube-TV asks for microphone access when search opens (voice search), and WebView2
            // prompts for it every time by default. Granted automatically so no prompt appears and voice
            // search works. Every other permission (camera, location, notifications) keeps the default.
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                {
                    e.State = CoreWebView2PermissionState.Allow;
                    e.Handled = true;
                }
            };

            // Escape and Backspace are marked Handled so the page does NOT also receive them: the shell
            // reacts and then injects the keypress itself through SendKey, exactly as it does for the
            // controller. One code path for both, instead of the page seeing a second, native "back".
            _controller.AcceleratorKeyPressed += (_, e) =>
            {
                var key = (int)e.VirtualKey;
                if (key is not (VkEscape or VkBack))
                {
                    return;
                }

                var down = e.KeyEventKind is CoreWebView2KeyEventKind.KeyDown
                                          or CoreWebView2KeyEventKind.SystemKeyDown;
                e.Handled = true;
                Dispatcher.UIThread.Post(() => ShellKey?.Invoke(key, down));
            };

            await core.AddScriptToExecuteOnDocumentCreatedAsync(InjectScript);

            _controller.IsVisible = true;
            UpdateBounds();
            core.Navigate(_url);
        }
        catch (Exception e)
        {
            // Usually the WebView2 runtime is missing; installing it is a job for the helper service.
            Dispatcher.UIThread.Post(() => InitFailed?.Invoke(e.Message));
        }
    }

    // Matches the browser rectangle to the real size (physical pixels) of the child window.
    private void UpdateBounds()
    {
        if (_controller is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (GetClientRect(_hwnd, out var r))
        {
            _controller.Bounds = new global::System.Drawing.Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    /// <summary>Injects a keypress into the page (controller -> key). No-op until the browser is ready.</summary>
    public void SendKey(int keyCode)
    {
        var core = _controller?.CoreWebView2;
        _ = core?.ExecuteScriptAsync($"window.__playfrontKey({keyCode})");
    }

    /// <summary>
    /// Parks the browser when focus is lost (a game is in the foreground): suspends its activity and
    /// drops its memory use. Undone by <see cref="Resume"/>. Does not close it.
    /// </summary>
    public void Suspend()
    {
        if (_controller?.CoreWebView2 is not { } core)
        {
            return;
        }

        _controller.IsVisible = false;
        _ = core.TrySuspendAsync();
    }

    /// <summary>Resumes after <see cref="Suspend"/> (Playfront is in the foreground again).</summary>
    public void Resume()
    {
        if (_controller?.CoreWebView2 is not { } core)
        {
            return;
        }

        core.Resume();
        _controller.IsVisible = true;
        UpdateBounds();
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        DisposeController();
        base.DestroyNativeControlCore(control);
    }

    private void DisposeController()
    {
        _disposed = true;
        if (_controller is not null)
        {
            // Closing the controller takes the browser processes (msedgewebview2) and their memory with
            // it: this is how the whole browser is released on leaving the screen.
            _controller.Close();
            _controller = null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Minimal observer for GetObservable, to avoid taking a dependency on System.Reactive.
    private sealed class AnonymousObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;
        public AnonymousObserver(Action<T> onNext) => _onNext = onNext;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => _onNext(value);
    }
}
