using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerOnQ.Infrastructure.Updates;

public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public FileSignatureVerification Verify(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!OperatingSystem.IsWindows())
            return new FileSignatureVerification(false, null, "Authenticode verification requires Windows.");
        if (!File.Exists(filePath))
            return new FileSignatureVerification(false, null, "The signed file does not exist.");

        var fileInfo = new WinTrustFileInfo(filePath);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = WinTrustData.ForFile(fileInfoPointer);
            var result = WinVerifyTrust(new nint(-1), GenericVerifyV2, ref data);
            data.StateAction = WinTrustDataStateAction.Close;
            _ = WinVerifyTrust(new nint(-1), GenericVerifyV2, ref data);
            if (result != 0)
                return new FileSignatureVerification(false, null, new Win32Exception(result).Message);

#pragma warning disable SYSLIB0057 // No X509CertificateLoader API extracts an Authenticode signer from a PE/MSI.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            return new FileSignatureVerification(true, fingerprint, null);
        }
        catch (Exception ex) when (ex is CryptographicException or Win32Exception)
        {
            return new FileSignatureVerification(false, null, ex.Message);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(nint window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public uint Size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public nint FileHandle;
        public nint KnownSubject;

        public WinTrustFileInfo(string filePath) => FilePath = filePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public WinTrustDataUiChoice UiChoice;
        public WinTrustDataRevocationChecks RevocationChecks;
        public WinTrustDataChoice UnionChoice;
        public nint FileInfo;
        public WinTrustDataStateAction StateAction;
        public nint StateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UrlReference;
        public WinTrustDataProviderFlags ProviderFlags;
        public uint UiContext;

        public static WinTrustData ForFile(nint fileInfo) => new()
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = WinTrustDataUiChoice.None,
            RevocationChecks = WinTrustDataRevocationChecks.WholeChain,
            UnionChoice = WinTrustDataChoice.File,
            FileInfo = fileInfo,
            StateAction = WinTrustDataStateAction.Verify,
            ProviderFlags = WinTrustDataProviderFlags.RevocationCheckChainExcludeRoot,
        };
    }

    private enum WinTrustDataUiChoice : uint { None = 2 }
    private enum WinTrustDataRevocationChecks : uint { WholeChain = 1 }
    private enum WinTrustDataChoice : uint { File = 1 }
    private enum WinTrustDataStateAction : uint { Verify = 1, Close = 2 }
    [Flags]
    private enum WinTrustDataProviderFlags : uint { RevocationCheckChainExcludeRoot = 0x80 }
}
