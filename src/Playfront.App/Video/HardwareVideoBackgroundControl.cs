using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Playfront.App.Video;

/// <summary>
/// Fondo de video decodificado por hardware (el chip de video dedicado de la GPU, via Media
/// Foundation - el mismo sistema que usa la propia Xbox) y entregado a Avalonia sin copiar
/// nunca los pixeles a memoria normal de CPU. Sustituye al reproductor por software anterior
/// (que llegaba a usar ~60% de un nucleo de CPU sin parar); medido con este: ~23% de CPU y
/// bastante menos RAM.
///
/// Como funciona: se crean dos cosas D3D11 (el "lenguaje" con el que se habla con la tarjeta
/// grafica) - un dispositivo con la decodificacion de video activada (que usa Media Foundation
/// para descodificar cada fotograma directamente en la GPU) y una textura "de recurso
/// compartido" marcada para que Avalonia pueda importarla y pintarla directamente. Cada
/// fotograma nuevo se "dibuja" (no se copia byte a byte - los formatos de pixel de Media
/// Foundation y de Avalonia no coinciden exactamente aunque son casi identicos) sobre esa
/// textura compartida con un shader minimo, y un "mutex con llave" evita que Media Foundation
/// y Avalonia intenten leer/escribir la textura a la vez.
/// </summary>
public sealed class HardwareVideoBackgroundControl : GpuCompositionControlBase
{
    private readonly string _videoPath;

    // Ruta del video que se QUIERE reproducir (la puede cambiar SetVideoSource desde el hilo de UI en
    // cualquier momento, sin bloquear) y la que se esta reproduciendo AHORA (solo la toca el hilo de
    // bombeo). Cuando difieren, el hilo de bombeo cambia el lector a la nueva, reutilizando el mismo
    // dispositivo/textura/superficie - asi NO se crea y destruye un reproductor entero por cada cambio
    // de fondo (eso bloqueaba la pagina). Vale para videos del mismo tamaño (todos 1920x1080).
    private volatile string _requestedPath;
    private string? _activePath;

    // Se pone a true al arrancar y cada vez que se cambia de video; cuando el hilo de bombeo entrega el
    // PRIMER fotograma del video nuevo, dispara VideoReady (en el hilo de UI) y lo pone a false. Sirve
    // para que quien use el control muestre el reproductor justo cuando ya se ve el fotograma nuevo (no
    // antes, para que no se vea un instante el fotograma del video anterior).
    private volatile bool _firstFramePending;

    /// <summary>Se dispara (en el hilo de UI) cuando ya se ha entregado el primer fotograma del video
    /// actual - util para revelar el reproductor sin parpadeo tras un SetVideoSource.</summary>
    public event Action? VideoReady;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IMFDXGIDeviceManager? _deviceManager;
    private IMFSourceReader? _reader;

    private ID3D11Texture2D? _sharedTexture;
    private ID3D11RenderTargetView? _sharedRtv;
    private IDXGIKeyedMutex? _sharedMutex;
    private IntPtr _sharedHandle;
    private ICompositionImportedGpuImage? _imported;
    private PlatformGraphicsExternalImageProperties _importedProps;

    // "Blit" por shader en vez de copiar los bytes del fotograma directamente: Media Foundation
    // entrega el fotograma en formato B8G8R8X8 (RGB sin canal alfa util) pero Avalonia solo sabe
    // importar texturas compartidas en B8G8R8A8 (con alfa) - son identicos en memoria pero
    // Direct3D no deja copiarlos directamente entre "nombres" de formato distintos (falla en
    // silencio, sin excepcion, pantalla en negro). Dibujar con un shader en vez de copiar evita
    // el problema y sigue siendo 100% trabajo de la GPU.
    private ID3D11VertexShader? _blitVs;
    private ID3D11PixelShader? _blitPs;
    private ID3D11SamplerState? _blitSampler;

    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _target;

    private Thread? _pumpThread;
    private volatile bool _stop;
    private volatile bool _paused;

    public HardwareVideoBackgroundControl(string videoPath)
    {
        _videoPath = videoPath;
        _requestedPath = videoPath;
    }

    // Cambia el video que se reproduce SIN recrear el control (reutiliza dispositivo/textura/superficie).
    // El hilo de bombeo lo recoge en su siguiente vuelta y cambia el lector alli (en segundo plano, no
    // bloquea la UI). Instantaneo desde el hilo de UI: solo asigna un campo.
    public void SetVideoSource(string videoPath) => _requestedPath = videoPath;

    // Pausa/reanuda la DECODIFICACION sin desmontar nada: el hilo de bombeo sigue vivo y Resume()
    // reanuda al instante. Se usa cuando el video queda TAPADO por una pantalla opaca (Ajustes,
    // Personalization): no se ve, asi que decodificarlo solo gastaria GPU y bateria. No confundir con
    // descargar el video, que si desmonta el decodificador (eso se hace solo al perder el primer plano,
    // p.ej. al entrar en un juego).
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    protected override (bool success, string error) InitializeGraphicsResources(Compositor compositor,
        CompositionDrawingSurface surface, ICompositionGpuInterop interop)
    {
        if (!interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle))
            return (false, "DXGI shared handle import no soportado por este backend grafico");

        _interop = interop;
        _target = surface;

        try
        {
            const DeviceCreationFlags flags = DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport;
            _device = D3D11.D3D11CreateDevice(DriverType.Hardware, flags,
                FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0);
            _context = _device.ImmediateContext;

            using (var multithread = _device.QueryInterface<ID3D11Multithread>())
                multithread.SetMultithreadProtected(true);

            CreateBlitShaders();

            MediaFactory.MFStartup();

            _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            _deviceManager.ResetDevice(_device);

            _reader = CreateReader(_videoPath);
            _activePath = _videoPath;

            using (var currentType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                MediaFactory.MFGetAttributeSize(currentType, MfGuids.MF_MT_FRAME_SIZE, out var width, out var height);
                CreateSharedTexture(width == 0 ? 1920u : width, height == 0 ? 1080u : height);
            }

            _stop = false;
            _firstFramePending = true;
            _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "PlayfrontHwVideoPump" };
            _pumpThread.Start();

            return (true, string.Empty);
        }
        catch (Exception e)
        {
            return (false, e.ToString());
        }
    }

    private const string BlitShaderSource = """
        Texture2D srcTexture : register(t0);
        SamplerState srcSampler : register(s0);

        struct VsOutput
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        VsOutput VSMain(uint id : SV_VertexID)
        {
            VsOutput output;
            float2 uv = float2((id << 1) & 2, id & 2);
            output.uv = uv;
            output.pos = float4(uv.x * 2 - 1, 1 - uv.y * 2, 0, 1);
            return output;
        }

        float4 PSMain(VsOutput input) : SV_TARGET
        {
            // Avalonia importa la textura compartida "boca abajo" respecto al convenio normal
            // de Direct3D (fila 0 = arriba) - se invierte aqui la coordenada vertical al leer
            // para compensarlo.
            float2 uv = float2(input.uv.x, 1 - input.uv.y);
            return float4(srcTexture.Sample(srcSampler, uv).rgb, 1);
        }
        """;

    // Crea un lector (source reader) de Media Foundation para un video, con salida RGB32 decodificada
    // por hardware (via el device manager D3D ya creado). Se usa al arrancar y al cambiar de video en
    // caliente desde el hilo de bombeo (ver PumpLoop).
    private IMFSourceReader CreateReader(string path)
    {
        using var readerAttributes = MediaFactory.MFCreateAttributes(4);
        readerAttributes.Set(MfGuids.MF_SOURCE_READER_D3D_MANAGER, _deviceManager);
        readerAttributes.Set(MfGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, true);
        readerAttributes.Set(MfGuids.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, true);
        readerAttributes.Set(MfGuids.MF_SOURCE_READER_DISABLE_DXVA, false);

        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, readerAttributes);
        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
        outputType.Set(MfGuids.MF_MT_SUBTYPE, MfGuids.MFVideoFormat_RGB32);
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);
        return reader;
    }

    private void CreateBlitShaders()
    {
        var vsBytes = Vortice.D3DCompiler.Compiler.Compile(BlitShaderSource, "VSMain", "blit", "vs_4_0");
        var psBytes = Vortice.D3DCompiler.Compiler.Compile(BlitShaderSource, "PSMain", "blit", "ps_4_0");
        _blitVs = _device!.CreateVertexShader(vsBytes.Span);
        _blitPs = _device.CreatePixelShader(psBytes.Span);
        _blitSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Always,
            MaxLOD = float.MaxValue
        });
    }

    private void CreateSharedTexture(uint width, uint height)
    {
        ReleaseSharedTexture();

        var desc = new Texture2DDescription
        {
            Format = Format.B8G8R8A8_UNorm,
            Width = width,
            Height = height,
            ArraySize = 1,
            MipLevels = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex
        };
        _sharedTexture = _device!.CreateTexture2D(desc);
        _sharedRtv = _device.CreateRenderTargetView(_sharedTexture);
        _sharedMutex = _sharedTexture.QueryInterface<IDXGIKeyedMutex>();
        using (var dxgiResource = _sharedTexture.QueryInterface<IDXGIResource>())
        {
            _sharedHandle = dxgiResource.SharedHandle;
        }

        _importedProps = new PlatformGraphicsExternalImageProperties
        {
            Width = (int)width,
            Height = (int)height,
            Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm
        };

        // ImportImage exige el hilo de UI de Avalonia - por eso se hace aqui (en la
        // inicializacion, que ya corre en ese hilo) en vez de la primera vez que hace falta
        // desde el hilo de bombeo de video en segundo plano.
        _imported = _interop!.ImportImage(
            new PlatformHandle(_sharedHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle),
            _importedProps);
    }

    private void PumpLoop()
    {
        var clock = Stopwatch.StartNew();
        long? firstSampleTimestamp = null;
        var wasPaused = false;

        while (!_stop)
        {
            try
            {
                // Pausado (la home esta tapada por otra pantalla opaca): no se decodifica nada, solo se
                // comprueba de vez en cuando si hay que reanudar o parar. Asi un video invisible no gasta
                // GPU ni bateria. El hilo sigue vivo, por eso reanudar es instantaneo.
                if (_paused)
                {
                    wasPaused = true;
                    Thread.Sleep(30);
                    continue;
                }
                if (wasPaused)
                {
                    // Al reanudar, re-anclar el ritmo: si no, intentaria "recuperar" de golpe todo el
                    // tiempo pausado y haria un fast-forward. El video sigue donde se quedo (sin salto).
                    wasPaused = false;
                    firstSampleTimestamp = null;
                    clock.Restart();
                }

                // Cambio de video en caliente: si se ha pedido otro (SetVideoSource), se cambia el
                // lector AQUI, en este hilo de fondo (abrir el nuevo archivo no bloquea la UI),
                // reutilizando dispositivo/textura/superficie. Mientras se abre, la textura conserva el
                // ultimo fotograma del anterior (se ve congelado unas decimas), luego fluye el nuevo.
                var requested = _requestedPath;
                if (!string.Equals(requested, _activePath, StringComparison.Ordinal))
                {
                    _reader?.Dispose();
                    _reader = CreateReader(requested);
                    _activePath = requested;
                    _firstFramePending = true;
                    firstSampleTimestamp = null;
                    clock.Restart();
                }

                var sample = _reader!.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                    out _, out var streamFlags, out var timestamp);

                if ((streamFlags & SourceReaderFlag.EndOfStream) != 0)
                {
                    _reader.SetCurrentPosition(0);
                    firstSampleTimestamp = null;
                    clock.Restart();
                    continue;
                }

                if (sample == null)
                    continue;

                // Ritmo real del video: esperamos hasta que el reloj llegue a la marca de tiempo
                // del fotograma, si no Media Foundation los da tan rapido como puede
                // descodificar (mucho mas rapido que la reproduccion normal).
                firstSampleTimestamp ??= timestamp;
                var targetElapsedMs = (timestamp - firstSampleTimestamp.Value) / 10_000.0;
                var waitMs = targetElapsedMs - clock.Elapsed.TotalMilliseconds;
                if (waitMs > 1)
                    Thread.Sleep((int)waitMs);

                using (sample)
                using (var buffer = sample.ConvertToContiguousBuffer())
                using (var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>())
                {
                    var frameTexturePtr = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
                    using var frameTexture = new ID3D11Texture2D(frameTexturePtr);
                    var subresource = (uint)dxgiBuffer.SubresourceIndex;

                    using var frameSrv = _device!.CreateShaderResourceView(frameTexture, new ShaderResourceViewDescription
                    {
                        Format = frameTexture.Description.Format,
                        ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                        Texture2DArray = new Texture2DArrayShaderResourceView
                        {
                            MipLevels = 1,
                            MostDetailedMip = 0,
                            FirstArraySlice = subresource,
                            ArraySize = 1
                        }
                    });

                    _sharedMutex!.AcquireSync(0, -1);

                    _context!.OMSetRenderTargets(_sharedRtv);
                    _context.RSSetViewport(0, 0, _sharedTexture!.Description.Width, _sharedTexture.Description.Height);
                    _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    _context.VSSetShader(_blitVs);
                    _context.PSSetShader(_blitPs);
                    _context.PSSetShaderResource(0, frameSrv);
                    _context.PSSetSampler(0, _blitSampler);
                    _context.Draw(3, 0);
                    _context.PSSetShaderResource(0, null);
                    _context.Flush();

                    _sharedMutex.ReleaseSync(1);

                    // Si han pedido el apagado mientras descodificabamos/dibujabamos este fotograma
                    // (que es la mayor parte del tiempo de cada frame), salimos AQUI, antes de la
                    // entrega bloqueante a la UI de abajo. Esa entrega espera al hilo de UI; si el
                    // apagado ya esta esperando a este hilo (Join de 2s), ambos se bloquearian
                    // mutuamente hasta que expira ese cronometro. Cortar antes de entrar hace que el
                    // apagado retorne en un fotograma en el caso comun.
                    if (_stop)
                        break;

                    if (_target != null && _imported != null)
                    {
                        // UpdateWithKeyedMutexAsync tambien exige el hilo de UI de Avalonia - no
                        // se puede llamar directamente desde este hilo de bombeo en segundo plano.
                        Dispatcher.UIThread.InvokeAsync(() => _target.UpdateWithKeyedMutexAsync(_imported, 1, 0))
                            .GetAwaiter().GetResult();
                    }

                    // Primer fotograma del video actual ya entregado -> avisar (el Post de VideoReady va
                    // DESPUES del Post de presentacion de arriba, asi que cuando llegue, la superficie ya
                    // muestra el fotograma nuevo).
                    if (_firstFramePending)
                    {
                        _firstFramePending = false;
                        Dispatcher.UIThread.Post(() => CrashLog.Guard(() => VideoReady?.Invoke(), "video-ready"));
                    }
                }
            }
            catch (Exception)
            {
                // Durante el APAGADO (_stop = true, control retirado de la pantalla) los recursos que
                // usa este bucle -reader, textura compartida, superficie de Avalonia- se estan
                // liberando en el hilo de UI, asi que aqui saltan excepciones (objeto ya liberado).
                // Hay que capturarlas y salir del bucle limpiamente: una excepcion NO capturada en
                // este hilo de fondo tumba TODA la app (asi se cerraba al quitar el video de preview
                // al mover la seleccion o salir de "Dynamic backgrounds"). Antes el filtro
                // "when (!_stop)" dejaba pasar la excepcion en ese caso, que es justo el que crashea.
                if (_stop)
                {
                    break;
                }

                // Fuera del apagado, un fallo puntual se reintenta tras una pausa (comportamiento de
                // siempre).
                Thread.Sleep(50);
            }
        }
    }

    protected override void FreeGraphicsResources()
    {
        _stop = true;
        _pumpThread?.Join(TimeSpan.FromSeconds(2));

        _reader?.Dispose();
        _deviceManager?.Dispose();

        ReleaseSharedTexture();

        _blitSampler?.Dispose();
        _blitPs?.Dispose();
        _blitVs?.Dispose();

        _context?.Dispose();
        _device?.Dispose();

        // "Apaga" Media Foundation, equilibrando el MFStartup() de la inicializacion (encender/apagar
        // van contados: sin este, el contador solo sube en cada reproductor que se crea y nunca baja).
        // Va el ULTIMO, cuando ya se han soltado el lector y el device manager (que son objetos de Media
        // Foundation): apagar el motor con algo suyo aun vivo daria error.
        MediaFactory.MFShutdown();
    }

    private void ReleaseSharedTexture()
    {
        _sharedMutex?.Dispose();
        _sharedMutex = null;
        _sharedRtv?.Dispose();
        _sharedRtv = null;
        _sharedTexture?.Dispose();
        _sharedTexture = null;
        _sharedHandle = IntPtr.Zero;
        _imported = null;
    }
}
