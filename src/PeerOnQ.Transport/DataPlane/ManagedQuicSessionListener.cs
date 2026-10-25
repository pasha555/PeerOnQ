using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace PeerOnQ.Transport.DataPlane;

/// <summary>Authenticated native QUIC listener backed by the platform MsQuic runtime on Windows.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class ManagedQuicSessionListener : IAsyncDisposable
{
    private readonly QuicListener _listener;

    private ManagedQuicSessionListener(QuicListener listener) => _listener = listener;

    public static bool IsSupported => QuicListener.IsSupported && QuicConnection.IsSupported;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndPoint;

    public static async ValueTask<ManagedQuicSessionListener> ListenAsync(
        IPEndPoint localEndPoint,
        X509Certificate2 localCertificate,
        string expectedPeerCertificateSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentNullException.ThrowIfNull(localCertificate);
        EnsureSupported();
        QuicPeerAuthentication.ValidateLocalCertificate(localCertificate);
        var peerAuthentication = new QuicPeerAuthentication(expectedPeerCertificateSha256);
        var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = localEndPoint,
            ApplicationProtocols = [PeerOnQQuicProtocol.ApplicationProtocol],
            ListenBacklog = 8,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = PeerOnQQuicProtocol.ProtocolErrorCode,
                DefaultStreamErrorCode = PeerOnQQuicProtocol.ProtocolErrorCode,
                HandshakeTimeout = TimeSpan.FromSeconds(5),
                IdleTimeout = TimeSpan.FromSeconds(30),
                KeepAliveInterval = TimeSpan.FromSeconds(5),
                MaxInboundBidirectionalStreams = 0,
                MaxInboundUnidirectionalStreams = 32,
                InitialReceiveWindowSizes = CreateReceiveWindows(),
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = [PeerOnQQuicProtocol.ApplicationProtocol],
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ServerCertificate = localCertificate,
                    ClientCertificateRequired = true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = peerAuthentication.Validate,
                },
            }),
        }, cancellationToken);
        return new ManagedQuicSessionListener(listener);
    }

    public async ValueTask<ManagedQuicRemoteSessionTransport> AcceptAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = await _listener.AcceptConnectionAsync(cancellationToken);
        return ManagedQuicRemoteSessionTransport.Attach(connection);
    }

    public ValueTask DisposeAsync() => _listener.DisposeAsync();

    internal static QuicReceiveWindowSizes CreateReceiveWindows() => new()
    {
        Connection = 16 * 1024 * 1024,
        LocallyInitiatedBidirectionalStream = 64 * 1024,
        RemotelyInitiatedBidirectionalStream = 64 * 1024,
        UnidirectionalStream = 4 * 1024 * 1024,
    };

    internal static void EnsureSupported()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("The platform QUIC implementation is unavailable.");
    }
}
