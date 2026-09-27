using PeerOnQ.Application.Security;
using PeerOnQ.Transport.Protocol;

namespace PeerOnQ.Platform.Windows;

/// <summary>
/// Capabilities backed by the current native Windows composition. Keep this declaration beside
/// the platform implementation so shared transports never assume that another OS behaves like Windows.
/// </summary>
public static class WindowsClientCapabilityProfile
{
    public static ClientCapabilityManifest Create()
    {
        List<string> capabilities =
        [
            EndpointCapabilityNames.ClipboardReceive,
            EndpointCapabilityNames.ClipboardSend,
            EndpointCapabilityNames.DisplaySelectAccept,
            EndpointCapabilityNames.DisplaySelectRequest,
            EndpointCapabilityNames.FileReceive,
            EndpointCapabilityNames.FileSend,
            EndpointCapabilityNames.InputInject,
            EndpointCapabilityNames.InputSend,
            EndpointCapabilityNames.ScreenCapture,
            EndpointCapabilityNames.ScreenRender,
            EndpointCapabilityNames.SessionHost,
            EndpointCapabilityNames.SessionReconnect,
            EndpointCapabilityNames.SessionViewer,
            EndpointCapabilityNames.UnattendedAccept,
            EndpointCapabilityNames.UnattendedRequest,
            EndpointCapabilityNames.SupportInvitationAccept,
            EndpointCapabilityNames.SupportInvitationRequest,
        ];
        if (PostQuantumCryptography.IsSupported)
            capabilities.Add(EndpointCapabilityNames.HybridPostQuantumSecure);

        return new ClientCapabilityManifest
        {
            Platform = PeerOnQClientPlatforms.Windows,
            Capabilities = capabilities,
            OptionalFeatures =
            [
                OptionalProtocolFeatureNames.FileRelay,
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
