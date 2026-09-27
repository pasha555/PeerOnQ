using PeerOnQ.Application.Security;
using PeerOnQ.Transport.Protocol;

namespace PeerOnQ.Platform.Linux;

/// <summary>
/// Capabilities implemented by the first native Linux client. It is intentionally a viewer:
/// Linux screen capture, input injection and unattended access are not advertised.
/// </summary>
public static class LinuxClientCapabilityProfile
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
            Platform = PeerOnQClientPlatforms.Linux,
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
