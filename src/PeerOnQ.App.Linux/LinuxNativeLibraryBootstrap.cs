using System.Runtime.InteropServices;
using SIPSorceryMedia.Encoders;

namespace PeerOnQ.App.Linux;

/// <summary>Uses the desktop distribution's maintained VP8 decoder, never a Windows or working-directory binary.</summary>
internal static class LinuxNativeLibraryBootstrap
{
    private static readonly Lock Gate = new();
    private static nint _library;

    internal static IReadOnlyList<string> CandidatePaths(Architecture architecture) => architecture switch
    {
        Architecture.X64 => ["/usr/lib/x86_64-linux-gnu/libvpx.so.9", "/lib/x86_64-linux-gnu/libvpx.so.9", "/usr/lib64/libvpx.so.9"],
        Architecture.Arm64 => ["/usr/lib/aarch64-linux-gnu/libvpx.so.9", "/lib/aarch64-linux-gnu/libvpx.so.9", "/usr/lib64/libvpx.so.9"],
        _ => [],
    };

    internal static void Initialize()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Linux VP8 decoder requires a Linux desktop.");
        lock (Gate)
        {
            if (_library != 0) return;
            foreach (var path in CandidatePaths(RuntimeInformation.ProcessArchitecture))
            {
                if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var library)) continue;
                try
                {
                    foreach (var export in new[] { "vpx_codec_vp8_dx", "vpx_codec_dec_init_ver", "vpx_codec_decode",
                                 "vpx_codec_get_frame", "vpx_codec_destroy", "vpx_codec_error" })
                        _ = NativeLibrary.GetExport(library, export);
                    NativeLibrary.SetDllImportResolver(typeof(VpxVideoEncoder).Assembly,
                        (name, _, _) => name.Equals("vpxmd", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("vpxmd.dll", StringComparison.OrdinalIgnoreCase) ? library : 0);
                    _library = library;
                    return;
                }
                catch
                {
                    NativeLibrary.Free(library);
                    throw;
                }
            }
            throw new PlatformNotSupportedException(
                "PeerOnQ requires the desktop distribution's libvpx.so.9 decoder. On Ubuntu 24.04, install libvpx9 and restart PeerOnQ.");
        }
    }
}
