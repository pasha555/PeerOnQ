using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class IceConfigurationTests
{
    [Fact]
    public void Relay_only_configuration_keeps_udp_tcp_and_tls_fallbacks()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var configuration = WebRtcMediaSession.BuildConfiguration(new IceConfiguration
        {
            TransportPolicy = IceTransportPolicy.RelayOnly,
            Servers =
            [
                new IceServerDefinition
                {
                    Urls =
                    [
                        "turn:turn.example.com:3478?transport=udp",
                        "turn:turn.example.com:3478?transport=tcp",
                        "turns:turn.example.com:5349?transport=tcp",
                    ],
                    Username = "expires:opaque",
                    Credential = "temporary",
                    CredentialExpiresAt = expires,
                },
            ],
        });

        Assert.Equal(RTCIceTransportPolicy.relay, configuration.iceTransportPolicy);
        Assert.Equal(3, configuration.iceServers.Count);
        Assert.Contains(configuration.iceServers, server => server.urls.Contains("transport=udp"));
        Assert.Contains(configuration.iceServers, server => server.urls.StartsWith("turns:"));
        Assert.All(configuration.iceServers, server => Assert.Equal("temporary", server.credential));
    }

    [Fact]
    public void Client_clock_skew_does_not_discard_server_issued_turn_credentials()
    {
        var configuration = WebRtcMediaSession.BuildConfiguration(new IceConfiguration
        {
            TransportPolicy = IceTransportPolicy.RelayOnly,
            Servers =
            [
                new IceServerDefinition
                {
                    Urls = ["turn:turn.example.com:3478"],
                    Username = "expired",
                    Credential = "expired",
                    CredentialExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                },
            ],
        });

        Assert.Equal(RTCIceTransportPolicy.relay, configuration.iceTransportPolicy);
        Assert.Single(configuration.iceServers);
        Assert.Equal("turn:turn.example.com:3478", configuration.iceServers[0].urls);
    }

    [Fact]
    public void File_transfer_permission_removes_turn_candidates_but_keeps_direct_discovery()
    {
        var configuration = WebRtcMediaSession.BuildConfiguration(
            new IceConfiguration
            {
                Servers =
                [
                    new IceServerDefinition
                    {
                        Urls =
                        [
                            "stun:stun.example.com:3478",
                            "turn:turn.example.com:3478?transport=udp",
                            "turns:turn.example.com:5349?transport=tcp",
                        ],
                        Username = "temporary",
                        Credential = "temporary",
                    },
                ],
            },
            SessionPermission.FileTransfer);

        Assert.Equal(RTCIceTransportPolicy.all, configuration.iceTransportPolicy);
        Assert.Single(configuration.iceServers);
        Assert.StartsWith("stun:", configuration.iceServers[0].urls, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Full_control_keeps_turn_candidates_for_screen_and_input()
    {
        var configuration = WebRtcMediaSession.BuildConfiguration(
            CreateTurnConfiguration(),
            Phase1SessionScope.FullControlPermissions);

        Assert.Equal(RTCIceTransportPolicy.all, configuration.iceTransportPolicy);
        Assert.Equal(3, configuration.iceServers.Count);
        Assert.Contains(configuration.iceServers, server =>
            server.urls.StartsWith("turn:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(configuration.iceServers, server =>
            server.urls.StartsWith("turns:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Relay_only_full_control_omits_the_direct_only_bulk_peer()
    {
        var iceConfiguration = CreateTurnConfiguration() with
        {
            TransportPolicy = IceTransportPolicy.RelayOnly,
        };
        var configuration = WebRtcMediaSession.BuildConfiguration(
            iceConfiguration,
            Phase1SessionScope.FullControlPermissions);
        await using var viewer = WebRtcMediaSession.CreateViewer(
            SessionId.New(),
            configuration: configuration,
            iceConfiguration: iceConfiguration,
            permissions: Phase1SessionScope.FullControlPermissions);
        await using var sharer = WebRtcMediaSession.CreateSharer(
            SessionId.New(),
            capture: null,
            MediaProfile.Conservative,
            configuration: configuration,
            iceConfiguration: iceConfiguration,
            permissions: Phase1SessionScope.FullControlPermissions);

        Assert.False(viewer.UsesDedicatedBulkPeerConnection);
        Assert.False(sharer.UsesDedicatedBulkPeerConnection);
    }

    [Fact]
    public void Relay_only_policy_rejects_file_transfer_before_peer_creation()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            WebRtcMediaSession.BuildConfiguration(
                new IceConfiguration
                {
                    TransportPolicy = IceTransportPolicy.RelayOnly,
                    Servers =
                    [
                        new IceServerDefinition
                        {
                            Urls = ["turn:turn.example.com:3478"],
                            Username = "temporary",
                            Credential = "temporary",
                        },
                    ],
                },
                SessionPermission.FileTransfer));

        Assert.Equal("direct_p2p_required", error.Message);
    }

    [Fact]
    public void Diagnostics_export_contains_no_candidate_addresses()
    {
        var export = ConnectionDiagnosticsExporter.ExportSanitized(new MediaStatistics
        {
            ConnectionPath = ConnectionPath.Relayed,
            RelayServerId = "turn-1",
            RelayRegion = "eu-west",
            RttMs = 42,
            SourceWidth = 2560,
            SourceHeight = 1440,
            RequestedWidth = 1920,
            RequestedHeight = 1080,
            EncodedWidth = 1280,
            EncodedHeight = 720,
            EncoderName = "SoftwareVp8",
            EncoderHardwareAccelerated = false,
            InputDataLaneNegotiated = true,
            InputDataLaneReady = true,
            InputDataRecordsSent = 23,
            BulkDataLaneNegotiated = true,
            BulkDataLaneReady = true,
            NativeBulkTransportNegotiated = true,
            NativeBulkTransportReady = true,
            NativeBulkBudgetKbps = 211_944,
            NativeBulkGoodputKbps = 205_000,
            NativeBulkFeedbackSamples = 17,
            InteractiveSctpBufferedBytes = 7,
            InputSctpBufferedBytes = 3,
            BulkSctpBufferedBytes = 11,
        });

        Assert.Contains("turn-1", export);
        Assert.Contains("\"sourceWidth\": 2560", export);
        Assert.Contains("\"encodedWidth\": 1280", export);
        Assert.Contains("\"encoderName\": \"SoftwareVp8\"", export);
        Assert.Contains("\"inputDataLaneNegotiated\": true", export);
        Assert.Contains("\"inputDataLaneReady\": true", export);
        Assert.Contains("\"inputDataRecordsSent\": 23", export);
        Assert.Contains("\"bulkDataLaneNegotiated\": true", export);
        Assert.Contains("\"nativeBulkTransportNegotiated\": true", export);
        Assert.Contains("\"nativeBulkTransportReady\": true", export);
        Assert.Contains("\"nativeBulkBudgetKbps\": 211944", export);
        Assert.Contains("\"nativeBulkGoodputKbps\": 205000", export);
        Assert.Contains("\"nativeBulkFeedbackSamples\": 17", export);
        Assert.Contains("\"interactiveSctpBufferedBytes\": 7", export);
        Assert.Contains("\"inputSctpBufferedBytes\": 3", export);
        Assert.Contains("\"bulkSctpBufferedBytes\": 11", export);
        Assert.DoesNotContain("candidate", export, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address", export, StringComparison.OrdinalIgnoreCase);
    }

    private static IceConfiguration CreateTurnConfiguration() => new()
    {
        Servers =
        [
            new IceServerDefinition
            {
                Urls =
                [
                    "stun:stun.example.com:3478",
                    "turn:turn.example.com:3478?transport=udp",
                    "turns:turn.example.com:5349?transport=tcp",
                ],
                Username = "temporary",
                Credential = "temporary",
            },
        ],
    };
}
