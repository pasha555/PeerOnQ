namespace PeerOnQ.Media.Pipeline;

public enum VideoEncoderKind
{
    /// <summary>libvpx VP8, always available.</summary>
    SoftwareVp8 = 0,

    /// <summary>Media Foundation hardware H.264.</summary>
    HardwareH264 = 1,
}

/// <summary>What the session will actually encode with, and why.</summary>
public sealed record EncoderSelection(VideoEncoderKind Kind, bool IsHardware, string Reason)
{
    public string Describe() => IsHardware
        ? $"{Kind} (hardware): {Reason}"
        : $"{Kind} (software): {Reason}";
}

/// <summary>
/// Chooses the video encoder for a session.
///
/// The policy is deliberately conservative: hardware is only chosen when the platform reports
/// a usable hardware encoder AND an implementation for it is present. Anything else falls back
/// to software VP8, and the reason is reported rather than hidden, so the UI never claims
/// acceleration that is not running.
/// </summary>
public sealed class VideoEncoderSelector(
    Func<bool> hardwareH264Available,
    Func<bool>? hardwareH264Implemented = null)
{
    /// <summary>
    /// The shipping codec path has no Media Foundation encoder wrapper, so this is false. Hardware
    /// detection still runs, which makes the production software fallback explicit and observable.
    /// </summary>
    private readonly Func<bool> _implemented = hardwareH264Implemented ?? (() => false);

    public EncoderSelection Select(bool preferHardware = true)
    {
        if (!preferHardware)
        {
            return new EncoderSelection(
                VideoEncoderKind.SoftwareVp8, false, "hardware encoding was disabled for this session");
        }

        bool available;
        try
        {
            available = hardwareH264Available();
        }
        catch (Exception ex)
        {
            return new EncoderSelection(
                VideoEncoderKind.SoftwareVp8, false, $"hardware probe failed ({ex.GetType().Name})");
        }

        if (!available)
        {
            return new EncoderSelection(
                VideoEncoderKind.SoftwareVp8, false, "no hardware H.264 encoder was reported by the platform");
        }

        if (!_implemented())
        {
            return new EncoderSelection(
                VideoEncoderKind.SoftwareVp8,
                false,
                "a hardware H.264 encoder exists but PeerOnQ has no encoder implementation in this build");
        }

        return new EncoderSelection(VideoEncoderKind.HardwareH264, true, "hardware H.264 encoder selected");
    }
}
