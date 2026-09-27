using PeerOnQ.Application.Security;
using PeerOnQ.Transport.Protocol;

namespace PeerOnQ.Platform.Android;

/// <summary>
/// Capabilities implemented by the attended Android viewer/controller. Hosting, local capture,
/// input injection, file transfer, clipboard and unattended access are intentionally absent.
/// </summary>
public static class AndroidClientCapabilityProfile
{
    public static ClientCapabilityManifest Create()
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
            Platform = PeerOnQClientPlatforms.Android,
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
}
