using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Playfront.App.Video;

// Genera el poster (primer fotograma) de un video usando EXACTAMENTE el mismo decodificador que lo
// reproduce en la home (Media Foundation por hardware, salida RGB32), no ffmpeg. Media Foundation y
// ffmpeg convierten el color YUV->RGB de forma un poco distinta (rango/matriz de BT.709), y por eso el
// poster sacado con ffmpeg no cuadra en color con el video al arrancar. Sacando el poster por ESTA via,
// coincide por construccion.
//
// Escribe los pixeles en crudo (BGRX, tal cual los da el decodificador) a un archivo; un paso posterior
// con ffmpeg los pasa a JPG/PNG. Es un modo headless (sin ventana): se dispara con la variable de
// entorno PLAYFRONT_CAPTURE_POSTER en Program.cs, decodifica un fotograma y sale, sin arrancar la interfaz.
internal static class PosterCapture
{
    // Decodifica el primer fotograma de videoPath y escribe sus pixeles BGRX en crudo a rawOutputPath.
    // Devuelve el tamaño (para poder reconstruir la imagen con ffmpeg despues).
    public static (int width, int height) CaptureFirstFrameRaw(string videoPath, string rawOutputPath)
    {
        const DeviceCreationFlags flags = DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport;
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, flags,
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0);
        var context = device.ImmediateContext;
        using (var multithread = device.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);

        MediaFactory.MFStartup();

        using var deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        deviceManager.ResetDevice(device);

        using var attrs = MediaFactory.MFCreateAttributes(4);
        attrs.Set(MfGuids.MF_SOURCE_READER_D3D_MANAGER, deviceManager);
        attrs.Set(MfGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, true);
        attrs.Set(MfGuids.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, true);
        attrs.Set(MfGuids.MF_SOURCE_READER_DISABLE_DXVA, false);

        using var reader = MediaFactory.MFCreateSourceReaderFromURL(videoPath, attrs);
        using (var outputType = MediaFactory.MFCreateMediaType())
        {
            outputType.Set(MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
            outputType.Set(MfGuids.MF_MT_SUBTYPE, MfGuids.MFVideoFormat_RGB32);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);
        }

        using (var currentType = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            MediaFactory.MFGetAttributeSize(currentType, MfGuids.MF_MT_FRAME_SIZE, out _, out _);

        // Leer hasta el primer sample con imagen (los primeros ReadSample pueden venir vacios).
        IMFSample? sample = null;
        for (var i = 0; i < 120 && sample == null; i++)
        {
            sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                out _, out var streamFlags, out _);
            if ((streamFlags & SourceReaderFlag.EndOfStream) != 0)
                break;
        }
        if (sample == null)
            throw new InvalidOperationException("No se pudo leer el primer fotograma del video.");

        using (sample)
        using (var buffer = sample.ConvertToContiguousBuffer())
        using (var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>())
        {
            var framePtr = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
            using var frameTexture = new ID3D11Texture2D(framePtr);
            var subresource = dxgiBuffer.SubresourceIndex;
            var desc = frameTexture.Description;

            // Textura "staging" (legible por CPU) del mismo formato, para copiar el fotograma y leerlo.
            var stagingDesc = new Texture2DDescription
            {
                Format = desc.Format,
                Width = desc.Width,
                Height = desc.Height,
                ArraySize = 1,
                MipLevels = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };
            using var staging = device.CreateTexture2D(stagingDesc);
            context.CopySubresourceRegion(staging, 0, 0, 0, 0, frameTexture, subresource);
            context.Flush();

            var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var w = (int)desc.Width;
                var h = (int)desc.Height;
                var row = new byte[w * 4];
                var rowPitch = (int)map.RowPitch;
                using var fs = File.Create(rawOutputPath);
                for (var y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(map.DataPointer, y * rowPitch), row, 0, w * 4);
                    fs.Write(row, 0, w * 4);
                }

                return (w, h);
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
    }
}
