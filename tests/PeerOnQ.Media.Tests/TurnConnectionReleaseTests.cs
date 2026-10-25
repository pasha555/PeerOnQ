using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PeerOnQ.Media;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

/// <summary>
/// 0.9.86 field test: after each closed session both clients still held their TLS connections to
/// coturn on port 5349 ("2 TCP connections to TURN ports are still open 5 s later"), and coturn
/// kept those allocations against user-quota=4. SIPSorcery's ICE channel closes the socket of a
/// turn:?transport=tcp server through its TCP receiver, but a turns: server has none.
/// </summary>
public sealed class TurnConnectionReleaseTests
{
    [Fact]
    public async Task Closing_a_session_closes_its_tls_connection_to_the_turn_server()
    {
        using var certificate = CreateServerCertificate();
        using var turnServer = new TcpListener(IPAddress.Loopback, 0);
        turnServer.Start();
        var port = ((IPEndPoint)turnServer.LocalEndpoint).Port;
        var peer = CreatePeer($"turns:127.0.0.1:{port}?transport=tcp");
        try
        {
            await StartGatheringAsync(peer);
            using var accepted = await turnServer.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var tls = new SslStream(new NetworkStream(accepted, ownsSocket: false));
            await tls.AuthenticateAsServerAsync(certificate).WaitAsync(TimeSpan.FromSeconds(10));

            peer.close();
            // The leak: closing the peer connection alone leaves the TLS connection open.
            Assert.False(await RemoteClosedAsync(tls, TimeSpan.FromMilliseconds(500)));

            Assert.Equal(1, WebRtcMediaSession.ReleaseTurnConnections(peer, null));
            Assert.True(await RemoteClosedAsync(tls, TimeSpan.FromSeconds(5)));
            // Close, then dispose: the second release finds nothing left.
            Assert.Equal(0, WebRtcMediaSession.ReleaseTurnConnections(peer));
        }
        finally
        {
            peer.close();
            peer.Dispose();
        }
    }

    [Fact]
    public async Task A_plain_tcp_turn_connection_is_already_closed_with_the_peer_connection()
    {
        using var turnServer = new TcpListener(IPAddress.Loopback, 0);
        turnServer.Start();
        var port = ((IPEndPoint)turnServer.LocalEndpoint).Port;
        var peer = CreatePeer($"turn:127.0.0.1:{port}?transport=tcp");
        try
        {
            await StartGatheringAsync(peer);
            using var accepted = await turnServer.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var stream = new NetworkStream(accepted, ownsSocket: false);

            peer.close();

            Assert.True(await RemoteClosedAsync(stream, TimeSpan.FromSeconds(5)));
            Assert.Equal(0, WebRtcMediaSession.ReleaseTurnConnections(peer));
        }
        finally
        {
            peer.close();
            peer.Dispose();
        }
    }

    private static RTCPeerConnection CreatePeer(string turnUrl) => new(new RTCConfiguration
    {
        iceServers =
        [
            new RTCIceServer
            {
                urls = turnUrl,
                username = "1790000000:session",
                credential = "test-credential",
            },
        ],
    });

    private static async Task StartGatheringAsync(RTCPeerConnection peer)
    {
        await peer.createDataChannel("probe");
        await peer.setLocalDescription(peer.createOffer());
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=turn.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // SslStream on Windows needs a persisted key, not the ephemeral one CreateSelfSigned returns.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>True when the client side closed the connection: a read ends (0 bytes) or fails.</summary>
    private static async Task<bool> RemoteClosedAsync(Stream stream, TimeSpan timeout)
    {
        var buffer = new byte[2048];
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                // TURN allocate requests and retries may arrive first; read until the stream ends.
                var read = await stream.ReadAsync(buffer, deadline.Token);
                if (read == 0) return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
