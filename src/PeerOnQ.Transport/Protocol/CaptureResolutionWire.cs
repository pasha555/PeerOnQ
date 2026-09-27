using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Errors;

namespace PeerOnQ.Transport.Protocol;

/// <summary>Strict wire names for an optional, non-upscaling capture-resolution preference.</summary>
public static class CaptureResolutionWire
{
    public static string Format(CaptureResolution resolution) => resolution switch
    {
        CaptureResolution.Automatic => "automatic",
        CaptureResolution.P720 => "720p",
        CaptureResolution.P1080 => "1080p",
        CaptureResolution.P1440 => "1440p",
        CaptureResolution.P2160 => "2160p",
        CaptureResolution.Native => "native",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
    };

    public static bool TryParse(string? value, out CaptureResolution resolution)
    {
        switch (value)
        {
            case "automatic":
                resolution = CaptureResolution.Automatic;
                return true;
            case "720p":
                resolution = CaptureResolution.P720;
                return true;
            case "1080p":
                resolution = CaptureResolution.P1080;
                return true;
            case "1440p":
                resolution = CaptureResolution.P1440;
                return true;
            case "2160p":
                resolution = CaptureResolution.P2160;
                return true;
            case "native":
                resolution = CaptureResolution.Native;
                return true;
            default:
                resolution = default;
                return false;
        }
    }

    public static CaptureResolution Parse(string value) =>
        TryParse(value, out var resolution)
            ? resolution
            : throw new SignalingProtocolException("The server supplied an unsupported capture resolution.");
}
