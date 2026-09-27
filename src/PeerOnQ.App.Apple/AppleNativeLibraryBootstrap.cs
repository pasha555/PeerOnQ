using System.Reflection;
using System.Runtime.InteropServices;
using SIPSorceryMedia.Encoders;

namespace PeerOnQ.App.Apple;

/// <summary>Resolves the existing libvpx binding against the statically linked Apple archive.</summary>
internal static class AppleNativeLibraryBootstrap
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(VpxVideoEncoder).Assembly, Resolve);
    }

    private static nint Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath) =>
        libraryName.Equals("vpxmd", StringComparison.OrdinalIgnoreCase)
            ? NativeLibrary.GetMainProgramHandle()
            : nint.Zero;
}
