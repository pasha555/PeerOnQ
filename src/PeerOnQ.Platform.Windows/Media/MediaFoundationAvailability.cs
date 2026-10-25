using System.Runtime.InteropServices;
using PeerOnQ.Media.Codecs;

namespace PeerOnQ.Platform.Windows.Media;

/// <summary>
/// Windows N editions ship without Media Foundation unless the Media Feature Pack is installed.
/// Without it there is no H.264 decoder or hardware encoder, so every session falls back to
/// software VP8; a field viewer (Windows 10 Pro N, i5-5300U) could not render 1080p30 that way.
/// </summary>
/// <remarks>
/// The pack is not required: capture (WGC + D3D11 video processor or CPU conversion), libvpx VP8,
/// WASAPI audio and both presenters work without it. Every Media Foundation entry point is gated
/// here by DLL presence, so a Windows N machine never attempts an MF call that can only fail.
/// The answer is cached: installing the pack needs a restart anyway.
/// </remarks>
public static class MediaFoundationAvailability
{
    /// <summary>Shown on the dashboard instead of an install request: the product works without the pack.</summary>
    public const string SoftwareVideoNotice =
        "Windows N: hardware video is not available on this edition, so PeerOnQ uses software video. Sessions work as usual.";

    private static readonly Lazy<Status> Current = new(() => Evaluate(TryLoad, File.Exists));

    internal readonly record struct Status(bool PlatformPresent, bool H264DecoderPresent)
    {
        public string? MissingReason =>
            !PlatformPresent ? "Media Foundation (mfplat.dll) is not installed"
            : !H264DecoderPresent ? "the Microsoft H.264 decoder (msmpeg2vdec.dll) is not installed"
            : null;
    }

    /// <summary>Null when Media Foundation and the H.264 decoder are present; otherwise why not.</summary>
    public static string? MissingReason() => Current.Value.MissingReason;

    /// <summary>True when mfplat.dll loads, so hardware H.264 encoders may be probed and created.</summary>
    public static bool IsPlatformPresent => Current.Value.PlatformPresent;

    /// <summary>True when an H.264 decoder can be created (platform and msmpeg2vdec.dll present).</summary>
    public static bool IsH264DecoderPresent => Current.Value is { PlatformPresent: true, H264DecoderPresent: true };

    internal static Status Evaluate(Func<string, bool> canLoad, Func<string, bool> fileExists)
    {
        var platform = canLoad("mfplat.dll");
        var decoder = platform && fileExists(Path.Combine(Environment.SystemDirectory, "msmpeg2vdec.dll"));
        return new Status(platform, decoder);
    }

    private static bool TryLoad(string library)
    {
        if (!NativeLibrary.TryLoad(library, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }
}

/// <summary>
/// The Media Foundation codec factories the media engine receives. A factory is null when its
/// component is missing (Windows N without the Media Feature Pack): the sharer then never offers
/// hardware H.264 and the viewer never offers H.264 decoding, so both sides negotiate VP8 without
/// attempting an MF call per session.
/// </summary>
public static class MediaFoundationCodecFactories
{
    public static Func<IHardwareH264Encoder?>? HardwareH264Encoder() =>
        HardwareH264Encoder(MediaFoundationAvailability.IsPlatformPresent);

    public static Func<IH264VideoDecoder?>? H264Decoder(Action? created = null) =>
        H264Decoder(MediaFoundationAvailability.IsH264DecoderPresent, created);

    internal static Func<IHardwareH264Encoder?>? HardwareH264Encoder(bool platformPresent) =>
        platformPresent ? () => new MediaFoundationH264Encoder() : null;

    internal static Func<IH264VideoDecoder?>? H264Decoder(bool decoderPresent, Action? created) =>
        decoderPresent
            ? () =>
            {
                var decoder = MediaFoundationH264Decoder.TryCreate();
                created?.Invoke();
                return decoder;
            }
            : null;
}
