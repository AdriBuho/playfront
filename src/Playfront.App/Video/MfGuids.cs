using System;

namespace Playfront.App.Video;

// Media Foundation GUIDs that Vortice doesn't already expose as named constants. Taken verbatim from
// the official Windows headers (mfapi.h / mfreadwrite.h). They cannot be guessed or approximated: one
// wrong digit and the corresponding attribute is simply not applied, with no visible error — the
// symptom is "behaves oddly", not "crashes".
internal static class MfGuids
{
    public static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid MF_SOURCE_READER_DISABLE_DXVA = new("aa456cfd-3943-4a1e-a77d-1838c0ea2e35");
    public static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");

    // D3DFMT_X8R8G8B8 = 22 (0x16) -> RGB32 with no meaningful alpha; ours is always opaque.
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
}
