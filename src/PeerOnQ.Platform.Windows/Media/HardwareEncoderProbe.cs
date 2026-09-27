using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PeerOnQ.Platform.Windows.Media;

public enum HardwareVideoCodec
{
    H264 = 0,
    Hevc = 1,
}

public sealed record HardwareEncoderInfo(HardwareVideoCodec Codec, string Name);

/// <summary>
/// Asks Media Foundation which hardware video encoders this machine actually has.
///
/// The result is used to choose an encoder, and it is reported to the user as-is: PeerOnQ
/// never claims hardware acceleration it did not find.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HardwareEncoderProbe
{
    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_HEVC = new("43564548-0000-0010-8000-00AA00389B71");

    private const int MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    private const int MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
    private const int MF_VERSION = 0x00020070;
    private const int MFSTARTUP_LITE = 1;

    private static readonly Lock Gate = new();
    private static IReadOnlyList<HardwareEncoderInfo>? _cached;

    /// <summary>Hardware encoders reported by the OS. Empty means software only.</summary>
    public static IReadOnlyList<HardwareEncoderInfo> Available
    {
        get
        {
            lock (Gate)
            {
                return _cached ??= Enumerate();
            }
        }
    }

    public static bool Supports(HardwareVideoCodec codec) => Available.Any(e => e.Codec == codec);

    /// <summary>Forces a re-probe; used by tests and after a driver change.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = null;
        }
    }

    private static List<HardwareEncoderInfo> Enumerate()
    {
        var found = new List<HardwareEncoderInfo>();

        try
        {
            if (MFStartup(MF_VERSION, MFSTARTUP_LITE) != 0)
            {
                return found;
            }
        }
        catch (DllNotFoundException)
        {
            // Media Foundation is absent (for example on Windows N without the media pack).
            return found;
        }
        catch (EntryPointNotFoundException)
        {
            return found;
        }

        try
        {
            Probe(HardwareVideoCodec.H264, MFVideoFormat_H264, found);
            Probe(HardwareVideoCodec.Hevc, MFVideoFormat_HEVC, found);
        }
        finally
        {
            try
            {
                _ = MFShutdown();
            }
            catch (Exception)
            {
                // Shutting down a subsystem we may not have started is not worth failing on.
            }
        }

        return found;
    }

    private static void Probe(HardwareVideoCodec codec, Guid subtype, List<HardwareEncoderInfo> found)
    {
        var outputType = new MftRegisterTypeInfo { guidMajorType = MFMediaType_Video, guidSubtype = subtype };

        var hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
            pInputType: nint.Zero,
            pOutputType: ref outputType,
            pppMFTActivate: out var activatePointer,
            pnumMFTActivate: out var count);

        if (hr != 0 || count == 0 || activatePointer == nint.Zero)
        {
            if (activatePointer != nint.Zero) Marshal.FreeCoTaskMem(activatePointer);
            return;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                var activate = Marshal.ReadIntPtr(activatePointer, i * nint.Size);
                if (activate == nint.Zero) continue;

                try
                {
                    // Only the presence and codec are read. Pulling the friendly name would
                    // mean calling into IMFAttributes, and a wrong vtable slot there takes the
                    // whole process down - not worth it for a label.
                    found.Add(new HardwareEncoderInfo(codec, $"{codec} hardware encoder #{i + 1}"));
                }
                finally
                {
                    Marshal.Release(activate);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(activatePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(
        Guid guidCategory,
        int flags,
        nint pInputType,
        ref MftRegisterTypeInfo pOutputType,
        out nint pppMFTActivate,
        out int pnumMFTActivate);
}
