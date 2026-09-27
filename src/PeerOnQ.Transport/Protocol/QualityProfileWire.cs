using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Errors;

namespace PeerOnQ.Transport.Protocol;

/// <summary>Strict, version-stable wire representation for built-in media quality profiles.</summary>
public static class QualityProfileWire
{
    public static string Format(QualityProfile quality) => quality switch
    {
        QualityProfile.Automatic => "automatic",
        QualityProfile.Performance => "performance",
        QualityProfile.Balanced => "balanced",
        QualityProfile.Quality => "quality",
        QualityProfile.Office => "office",
        QualityProfile.LowBandwidth => "low-bandwidth",
        _ => throw new ArgumentOutOfRangeException(nameof(quality)),
    };

    /// <summary>A missing value is the legacy v1 representation of Automatic.</summary>
    public static bool TryParse(string? value, out QualityProfile quality)
    {
        switch (value)
        {
            case null:
            case "automatic":
                quality = QualityProfile.Automatic;
                return true;
            case "performance":
                quality = QualityProfile.Performance;
                return true;
            case "balanced":
                quality = QualityProfile.Balanced;
                return true;
            case "quality":
                quality = QualityProfile.Quality;
                return true;
            case "office":
                quality = QualityProfile.Office;
                return true;
            case "low-bandwidth":
                quality = QualityProfile.LowBandwidth;
                return true;
            default:
                quality = default;
                return false;
        }
    }

    public static QualityProfile Parse(string? value) =>
        TryParse(value, out var quality)
            ? quality
            : throw new SignalingProtocolException("The server supplied an unsupported quality profile.");
}
