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
/// Aloja un navegador WebView2 (el motor de Edge, ya instalado en la maquina) dentro de la interfaz de
/// Playfront, para cargar apps web tipo consola (de momento, la interfaz de TV de YouTube: youtube.com/tv).
///
/// Es un <see cref="NativeControlHost"/>: WebView2 es una VENTANA NATIVA de Windows, no un dibujo de
/// Avalonia. Avalonia crea una ventana hija y aqui, encima de ella, se monta el controlador de WebView2.
/// Consecuencia conocida ("airspace"): esta vista se dibuja SIEMPRE por encima de lo que pinte Avalonia,
/// asi que la pantalla de YouTube va a pantalla completa sin nada de Playfront superpuesto (ver PLAN-youtube).
///
/// El mando NO llega solo a la pagina (la interfaz Leanback se maneja con flechas+Enter, no con la
/// Gamepad API): Playfront traduce el mando a teclas y las inyecta con <see cref="SendKey"/>, que llama a un
/// ayudante de JavaScript pre-inyectado en la pagina.
///
/// Ciclo de vida acorde a la norma de rendimiento de Playfront: <see cref="Suspend"/> al perder el foco
/// (juego en primer plano) y liberacion completa en <see cref="Dispose"/> al salir de la pantalla.
/// </summary>
public sealed class WebViewHost : NativeControlHost
{
    // User-agent de consola: hace que YouTube sirva la interfaz de TV (Leanback) en vez de la web normal.
    // Cobalt 19 a proposito (evita que YouTube asuma DRM Widevine, que WebView2 no trae). Ver PLAN-youtube.
    private const string UserAgent =
        "Mozilla/5.0 (PS4; Leanback Shell) Cobalt/19.lts.0-qa; compatible; Playfront/1.0";

    private readonly string _url;
    private readonly string _profileFolder;

    // Ayudante JS pre-inyectado: Playfront lo llama con __playfrontKey(code) para simular una pulsacion de tecla
    // (keydown+keyup) que la interfaz Leanback entiende. Es el metodo fiable (no enviar WM_KEYDOWN al HWND).
    // OJO: cada evento se lanza UNA sola vez, y solo sobre 'document'. Lanzarlo tambien sobre
    // document.activeElement (como se hacia antes) lo duplicaba: la interfaz Leanback escucha en
    // 'document', y el evento del activeElement, al propagarse (bubbles), volvia a llegar a 'document'
    // -> cada pulsacion contaba DOBLE y bajaba/subia dos categorias. document.dispatchEvent con
    // bubbles:true ya alcanza a los listeners de 'document' y 'window', que es donde escucha Leanback.
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

    /// <summary>Se dispara si el navegador no se puede inicializar (p.ej. falta el runtime de WebView2).</summary>
    public event Action<string>? InitFailed;

    public WebViewHost(string url, string profileFolder)
    {
        _url = url;
        _profileFolder = profileFolder;

        // Cada vez que Avalonia recoloca/redimensiona esta vista, ajustar el tamano del navegador para
        // que llene el hueco (la ventana entera, en el caso de YouTube).
        this.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(_ => UpdateBounds()));
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent); // Avalonia crea la ventana hija (HWND)
        _hwnd = handle.Handle;
        _ = InitializeAsync(); // arranca la creacion del navegador (async); continua en el hilo de UI
        return handle;
    }

    private async Task InitializeAsync()
    {
        try
        {
            // userDataFolder = donde viven cookies y sesion (login persistente). NUNCA se borra en
            // limpiezas: borrarlo = re-emparejar la cuenta.
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _profileFolder,
                options: null);

            if (_disposed)
            {
                return; // se salio de la pantalla antes de que el navegador terminara de crearse
            }

            _controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            if (_disposed)
            {
                _controller.Close();
                _controller = null;
                return;
            }

            var core = _controller.CoreWebView2;

            // UA ANTES de navegar (aplica a todas las peticiones del control por igual).
            core.Settings.UserAgent = UserAgent;

            // Ajustes de "kiosco": nada de menus, teclas del navegador, devtools, zoom ni barras. Esto es
            // una app a pantalla completa manejada con mando, no un navegador.
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsSwipeNavigationEnabled = false;

            // MICROFONO: YouTube-TV pide permiso de microfono al abrir la busqueda (busqueda por voz).
            // Por defecto WebView2 lo PREGUNTA cada vez con un aviso. Como Playfront es una app tipo consola,
            // se concede automaticamente: sin aviso, y la busqueda por voz queda disponible. Otros
            // permisos (camara, ubicacion, notificaciones...) se dejan con el comportamiento por defecto.
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                {
                    e.State = CoreWebView2PermissionState.Allow;
                    e.Handled = true;
                }
            };

            await core.AddScriptToExecuteOnDocumentCreatedAsync(InjectScript);

            _controller.IsVisible = true;
            UpdateBounds();
            core.Navigate(_url);
        }
        catch (Exception e)
        {
            // Lo mas comun: falta el runtime de WebView2. Playfront lo instalaria via el ayudante (futuro).
            Dispatcher.UIThread.Post(() => InitFailed?.Invoke(e.Message));
        }
    }

    // Ajusta el rectangulo del navegador al tamano real (en pixeles fisicos) de la ventana hija.
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

    /// <summary>Inyecta una pulsacion de tecla en la pagina (mando -> tecla). No-op si aun no esta listo.</summary>
    public void SendKey(int keyCode)
    {
        var core = _controller?.CoreWebView2;
        _ = core?.ExecuteScriptAsync($"window.__playfrontKey({keyCode})");
    }

    /// <summary>
    /// "Aparca" el navegador al perder el foco (juego en primer plano): suspende su actividad y baja su
    /// uso de memoria. Se deshace con <see cref="Resume"/> al volver. No lo cierra.
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

    /// <summary>Reanuda tras <see cref="Suspend"/> (Playfront vuelve al primer plano).</summary>
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
            // Cerrar el controlador se lleva por delante los procesos del navegador (msedgewebview2) y su
            // memoria: es como Playfront suelta el navegador entero al salir de la pantalla.
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

    // Observador minimo para GetObservable (evita depender de System.Reactive).
    private sealed class AnonymousObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;
        public AnonymousObserver(Action<T> onNext) => _onNext = onNext;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => _onNext(value);
    }
}
