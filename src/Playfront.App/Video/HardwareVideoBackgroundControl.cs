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
/// Video background decoded in hardware (the GPU's dedicated video block, through Media Foundation)
/// and handed to Avalonia without ever copying pixels back to CPU memory. Replaces the earlier
/// software player, which sat at ~60% of a CPU core continuously; this one measures ~23% CPU and
/// noticeably less RAM.
///
/// How it works: two D3D11 objects are created - a device with video decoding enabled (Media
/// Foundation decodes each frame straight on the GPU) and a shared-resource texture flagged so
/// Avalonia can import and draw it directly. Each new frame is DRAWN onto that shared texture with a
/// minimal shader rather than copied byte for byte (Media Foundation's and Avalonia's pixel formats
/// are near-identical but not the same name), and a keyed mutex stops Media Foundation and Avalonia
/// reading/writing the texture at once.
/// </summary>
public sealed class HardwareVideoBackgroundControl : GpuCompositionControlBase
{
    private readonly string _videoPath;

    // Path of the video that SHOULD play (SetVideoSource can change it from the UI thread at any time
    // without blocking) and the one playing RIGHT NOW (touched only by the pump thread). When they
    // differ, the pump thread switches the reader over, reusing the same device/texture/surface - so a
    // whole player is NOT built and torn down per background change, which used to stall the page.
    // Valid for videos of the same size (all are 1920x1080).
    private volatile string _requestedPath;
    private string? _activePath;

    // Set true at startup and on every video change; when the pump thread delivers the FIRST frame of
    // the new video it raises VideoReady (on the UI thread) and clears it. Lets the caller reveal the
    // player exactly when the new frame is on screen, not before - otherwise the previous video's last
    // frame flashes.
    private volatile bool _firstFramePending;

    /// <summary>Raised on the UI thread once the current video's first frame has been delivered -
    /// used to reveal the player without a flash after SetVideoSource.</summary>
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

    // Shader blit instead of copying the frame bytes: Media Foundation delivers B8G8R8X8 (RGB, no
    // usable alpha) but Avalonia only imports shared textures as B8G8R8A8. They are identical in
    // memory, yet Direct3D refuses a direct copy between differently named formats - and it fails
    // SILENTLY, no exception, black screen. Drawing with a shader sidesteps that and stays entirely on
    // the GPU.
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

    // Changes the playing video WITHOUT recreating the control (device/texture/surface are reused).
    // The pump thread picks it up on its next pass and switches the reader there, off the UI thread.
    // Instant from the caller's side: it only assigns a field.
    public void SetVideoSource(string videoPath) => _requestedPath = videoPath;

    // Pauses/resumes DECODING without tearing anything down: the pump thread stays alive so Resume() is
    // instant. Used when the video is covered by an opaque screen (Settings, Personalization) - it is
    // not visible, so decoding it would only burn GPU and battery. Not to be confused with unloading
    // the video, which does tear the decoder down and happens only on losing the foreground, such as
    // entering a game.
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    protected override (bool success, string error) InitializeGraphicsResources(Compositor compositor,
        CompositionDrawingSurface surface, ICompositionGpuInterop interop)
    {
        if (!interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle))
            return (false, "DXGI shared handle import is not supported by this graphics backend");

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
            // Avalonia imports the shared texture flipped relative to the usual Direct3D convention
            // (row 0 = top), so the vertical coordinate is inverted here on read to compensate.
            float2 uv = float2(input.uv.x, 1 - input.uv.y);
            return float4(srcTexture.Sample(srcSampler, uv).rgb, 1);
        }
        """;

    // Creates a Media Foundation source reader for a video, with RGB32 output decoded in hardware
    // through the D3D device manager. Used at startup and on hot video switches from the pump thread.
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

        // ImportImage requires Avalonia's UI thread, hence doing it here during initialization (which
        // already runs there) rather than on first need from the background pump thread.
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
                // Paused (Home is covered by an opaque screen): nothing is decoded, just an occasional
                // check for resume or stop. An invisible video costs no GPU or battery. The thread stays
                // alive, which is why resuming is instant.
                if (_paused)
                {
                    wasPaused = true;
                    Thread.Sleep(30);
                    continue;
                }
                if (wasPaused)
                {
                    // On resume, re-anchor the clock: otherwise it would try to make up all the paused
                    // time at once and fast-forward. The video continues where it left off.
                    wasPaused = false;
                    firstSampleTimestamp = null;
                    clock.Restart();
                }

                // Hot video switch: if another one was requested (SetVideoSource), the reader is
                // swapped HERE, on this background thread, so opening the new file does not block the
                // UI. Device/texture/surface are reused. While it opens, the texture still holds the
                // previous last frame (frozen for a few tenths), then the new one flows.
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

                // Real playback pacing: wait until the clock reaches the frame's timestamp. Without
                // this, Media Foundation hands frames over as fast as it can decode them, far faster
                // than real time.
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

                    // If shutdown was requested while this frame was decoding/drawing (most of a
                    // frame's time), bail out HERE, before the blocking hand-off below. That hand-off
                    // waits on the UI thread; if shutdown is already waiting on this thread (a 2s
                    // Join), the two would block each other until that timer expires. Cutting out
                    // first makes shutdown return within a frame in the common case.
                    if (_stop)
                        break;

                    if (_target != null && _imported != null)
                    {
                        // UpdateWithKeyedMutexAsync also requires Avalonia's UI thread; it cannot be
                        // called directly from this background pump thread.
                        Dispatcher.UIThread.InvokeAsync(() => _target.UpdateWithKeyedMutexAsync(_imported, 1, 0))
                            .GetAwaiter().GetResult();
                    }

                    // First frame of the current video delivered -> notify. The VideoReady post is
                    // queued AFTER the present above, so by the time it runs the surface already shows
                    // the new frame.
                    if (_firstFramePending)
                    {
                        _firstFramePending = false;
                        Dispatcher.UIThread.Post(() => CrashLog.Guard(() => VideoReady?.Invoke(), "video-ready"));
                    }
                }
            }
            catch (Exception)
            {
                // During SHUTDOWN (_stop = true, control removed from the screen) the resources this
                // loop uses - reader, shared texture, Avalonia surface - are being freed on the UI
                // thread, so disposed-object exceptions land here. They must be caught and the loop
                // exited cleanly: an UNCAUGHT exception on this background thread takes the WHOLE app
                // down, which is how it used to close when the preview video was dropped on moving the
                // selection or leaving "Dynamic backgrounds". A "when (!_stop)" filter let the
                // exception through in exactly the case that crashes.
                if (_stop)
                {
                    break;
                }

                // Outside shutdown, a one-off failure is retried after a short pause.
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

        // Balances the MFStartup() from initialization - the startup/shutdown pair is refcounted, so
        // without this the count only ever climbs with each player created. Goes LAST, once the reader
        // and the device manager (both Media Foundation objects) are released: shutting the engine down
        // with one of its objects still alive errors out.
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
