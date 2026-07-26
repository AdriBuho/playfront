using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Playfront.Helper;

// Verifica que un ejecutable tiene una firma Authenticode VALIDA y de confianza (via WinVerifyTrust de
// Windows, que comprueba tanto la cadena de certificados como que el contenido no se ha manipulado) Y
// que el firmante es Valve. Es OBLIGATORIO antes de ejecutar el instalador de Steam: el ayudante corre
// como SYSTEM, asi que ejecutar un binario descargado sin verificar la firma seria un agujero grave.
internal static class SignatureVerifier
{
    public static bool VerifyValve(string filePath, out string signer)
    {
        signer = "(desconocido)";

        // (1) La firma es valida y de confianza (hash intacto + cadena hasta una CA de confianza).
        if (!IsAuthenticodeTrusted(filePath))
        {
            return false;
        }

        // (2) El firmante es Valve.
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            signer = cert.Subject;
            return signer.Contains("Valve", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAuthenticodeTrusted(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        IntPtr pData = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pUnion = pFile,
                dwStateAction = WtdStateActionVerify,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WtdSaferFlag,
                dwUIContext = 0,
            };
            pData = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, pData, false);

            var action = WinTrustActionGenericVerifyV2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, pData);

            // Cerrar el estado que abrio la verificacion (obligatorio para no filtrar).
            var closeData = Marshal.PtrToStructure<WinTrustData>(pData);
            closeData.dwStateAction = WtdStateActionClose;
            Marshal.StructureToPtr(closeData, pData, false);
            WinVerifyTrust(IntPtr.Zero, ref action, pData);

            return result == 0; // 0 = firma valida y de confianza
        }
        finally
        {
            if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
            Marshal.FreeHGlobal(pFile);
        }
    }

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x00000100;

    private static Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }
}
