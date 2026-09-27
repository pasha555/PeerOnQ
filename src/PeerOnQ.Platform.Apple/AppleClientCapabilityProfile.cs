using PeerOnQ.Application.Security;
using PeerOnQ.Transport.Protocol;

namespace PeerOnQ.Platform.Apple;

public enum AppleClientPlatform
{
    MacOS = 0,
    IOS = 1,
    IPadOS = 2,
}

/// <summary>
/// Capabilities implemented by the attended Apple viewer/controller. Hosting, local capture,
/// input injection, file transfer, clipboard and unattended access are intentionally absent.
/// </summary>
public static class AppleClientCapabilityProfile
{
    public static ClientCapabilityManifest Create(AppleClientPlatform platform)
    {
        List<string> capabilities =
        [
            EndpointCapabilityNames.InputSend,
            EndpointCapabilityNames.ScreenRender,
            EndpointCapabilityNames.SessionReconnect,
            EndpointCapabilityNames.SessionViewer,
        ];
        if (PostQuantumCryptography.IsSupported)
            capabilities.Add(EndpointCapabilityNames.HybridPostQuantumSecure);

        return new ClientCapabilityManifest
        {
            Platform = ToWirePlatform(platform),
            Capabilities = capabilities,
            OptionalFeatures =
            [
                OptionalProtocolFeatureNames.SafeUnknownMessages,
                OptionalProtocolFeatureNames.SessionCapabilityDetails,
            ],
            RequiredServerCapabilities =
            [
                SignalingServerCapabilityNames.AuthenticatedRegistration,
                SignalingServerCapabilityNames.SessionCapabilityGate,
                SignalingServerCapabilityNames.SessionRouting,
            ],
        };
    }

    public static string ToWirePlatform(AppleClientPlatform platform) => platform switch
    {
        AppleClientPlatform.MacOS => PeerOnQClientPlatforms.MacOS,
        AppleClientPlatform.IOS => PeerOnQClientPlatforms.IOS,
        AppleClientPlatform.IPadOS => PeerOnQClientPlatforms.IPadOS,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown Apple client platform."),
    };
}
