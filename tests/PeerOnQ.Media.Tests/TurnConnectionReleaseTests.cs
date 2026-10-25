using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PeerOnQ.Media;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

/// <summary>
/// 0.9.86 field test: after each closed session both clients still held their TLS connections to
/// coturn on port 5349 ("2 TCP connections to TURN ports are still open 5 s later"), and coturn
/// kept those allocations against user-quota=4. The SIPSorcery 8.0.14 derivative carries TURN over
/// TLS itself: it validates the server certificate (normal validation unless the opt-in
/// RtpIceChannel.DefaultTurnTlsCertificateValidation hook says otherwise) and closes the TLS
/// connection together with the peer connection, as it does for turn:?transport=tcp.
/// ReleaseTurnConnections stays as the safety net for sockets that are still open.
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
        // The self-signed test certificate is trusted only here, through the opt-in hook.
        RtpIceChannel.DefaultTurnTlsCertificateValidation = TrustOnly(certificate);
        var peer = CreatePeer($"turns:127.0.0.1:{port}?transport=tcp");
        try
        {
            await StartGatheringAsync(peer);
            using var accepted = await turnServer.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var tls = new SslStream(new NetworkStream(accepted, ownsSocket: false));
            await tls.AuthenticateAsServerAsync(certificate).WaitAsync(TimeSpan.FromSeconds(10));
            var turn = new UnauthorizedTurnServer(tls);

            // The client accepted the certificate and sent its TURN Allocate over TLS.
            await turn.FirstAllocate.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(turn.Closed.IsCompleted);

            peer.close();

            await turn.Closed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, WebRtcMediaSession.ReleaseTurnConnections(peer, null));
        }
        finally
        {
            RtpIceChannel.DefaultTurnTlsCertificateValidation = null;
            peer.close();
            peer.Dispose();
        }
    }

    [Fact]
    public async Task Releasing_turn_connections_closes_a_tls_connection_that_is_still_open()
    {
        using var certificate = CreateServerCertificate();
        using var turnServer = new TcpListener(IPAddress.Loopback, 0);
        turnServer.Start();
        var port = ((IPEndPoint)turnServer.LocalEndpoint).Port;
        RtpIceChannel.DefaultTurnTlsCertificateValidation = TrustOnly(certificate);
        var peer = CreatePeer($"turns:127.0.0.1:{port}?transport=tcp");
        try
        {
            await StartGatheringAsync(peer);
            using var accepted = await turnServer.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var tls = new SslStream(new NetworkStream(accepted, ownsSocket: false));
            await tls.AuthenticateAsServerAsync(certificate).WaitAsync(TimeSpan.FromSeconds(10));
            var turn = new UnauthorizedTurnServer(tls);
            await turn.FirstAllocate.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, WebRtcMediaSession.ReleaseTurnConnections(peer, null));
            await turn.Closed.WaitAsync(TimeSpan.FromSeconds(5));

            peer.close();
            Assert.Equal(0, WebRtcMediaSession.ReleaseTurnConnections(peer));
        }
        finally
        {
            RtpIceChannel.DefaultTurnTlsCertificateValidation = null;
            peer.close();
            peer.Dispose();
        }
    }

    [Fact]
    public async Task An_untrusted_turn_certificate_is_rejected_before_any_request()
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
            try
            {
                // With TLS 1.3 the server side may finish before the client rejects the certificate.
                await tls.AuthenticateAsServerAsync(certificate).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                return;
            }

            var buffer = new byte[2048];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int read;
            try
            {
                read = await tls.ReadAsync(buffer, deadline.Token);
            }
            catch (IOException)
            {
                read = 0;
            }

            Assert.Equal(0, read);
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

    /// <summary>Trusts exactly the test certificate; every other certificate keeps the normal rule.</summary>
    private static RemoteCertificateValidationCallback TrustOnly(X509Certificate2 certificate) =>
        (_, presented, _, errors) => errors == SslPolicyErrors.None
                                     || (presented is not null && presented.GetCertHashString() == certificate.GetCertHashString());

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

    /// <summary>
    /// Answers every TURN request with 401 (realm and nonce), which keeps the client's allocation
    /// attempt, and with it the TLS connection, alive without completing it.
    /// </summary>
    private sealed class UnauthorizedTurnServer
    {
        private readonly TaskCompletionSource _firstAllocate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UnauthorizedTurnServer(Stream stream) => Closed = ServeAsync(stream);

        public Task FirstAllocate => _firstAllocate.Task;

        /// <summary>Completes when the client closes the connection.</summary>
        public Task Closed { get; }

        private async Task ServeAsync(Stream stream)
        {
            var header = new byte[20];
            try
            {
                while (await ReadExactlyOrEndAsync(stream, header))
                {
                    var body = new byte[(header[2] << 8) | header[3]];
                    if (!await ReadExactlyOrEndAsync(stream, body)) return;
                    if (header[0] != 0x00 || header[1] != 0x03) continue; // Only Allocate requests are expected.

                    _firstAllocate.TrySetResult();
                    var unauthorized = new STUNMessage(STUNMessageTypesEnum.AllocateErrorResponse);
                    unauthorized.Header.TransactionId = header[8..20];
                    unauthorized.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.ErrorCode,
                        [0x00, 0x00, 0x04, 0x01, .. Encoding.UTF8.GetBytes("Unauthorized")]));
                    unauthorized.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Realm, Encoding.UTF8.GetBytes("turn.test")));
                    unauthorized.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Nonce, Encoding.UTF8.GetBytes("0123456789abcdef")));
                    await stream.WriteAsync(unauthorized.ToByteBuffer(null, false));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Closed by the client, or by the test when it ends.
            }
        }

        private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer)
        {
            for (var offset = 0; offset < buffer.Length;)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset));
                if (read == 0) return false;
                offset += read;
            }

            return true;
        }
    }
}
