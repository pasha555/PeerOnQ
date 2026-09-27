using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Transport.DataPlane;

/// <summary>
/// Creates session-ephemeral managed QUIC endpoints. The application layer owns authenticated
/// fingerprint/port exchange and activates this factory only for a negotiated same-LAN path.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class ManagedQuicBulkTransportFactory : INativeBulkTransportFactory
{
    public bool IsSupported => ManagedQuicSessionListener.IsSupported;

    public ValueTask<INativeBulkTransportEndpoint> CreateEndpointAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported)
            throw new PlatformNotSupportedException("The platform QUIC implementation is unavailable.");
        return ValueTask.FromResult<INativeBulkTransportEndpoint>(
            new ManagedQuicBulkTransportEndpoint(CreateCertificate()));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=PeerOnQ Ephemeral Session",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), // TLS server authentication
                new("1.3.6.1.5.5.7.3.2"), // TLS client authentication
            },
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(4));
        var pkcs12 = certificate.Export(X509ContentType.Pfx);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                password: null,
                X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    private sealed class ManagedQuicBulkTransportEndpoint(X509Certificate2 certificate)
        : INativeBulkTransportEndpoint
    {
        private int _disposed;

        public string CertificateSha256 { get; } =
            QuicPeerAuthentication.GetCertificateSha256(certificate);

        public async ValueTask<INativeBulkTransportListener> ListenAsync(
            IPAddress localAddress,
            string expectedPeerCertificateSha256,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(localAddress);
            var listener = await ManagedQuicSessionListener.ListenAsync(
                new IPEndPoint(localAddress, 0),
                certificate,
                expectedPeerCertificateSha256,
                cancellationToken);
            return new ManagedQuicBulkTransportListener(listener);
        }

        public async ValueTask<IReliableRemoteSessionTransport> ConnectAsync(
            IPEndPoint remoteEndPoint,
            string expectedPeerCertificateSha256,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await ManagedQuicRemoteSessionTransport.ConnectAsync(
                remoteEndPoint,
                certificate,
                expectedPeerCertificateSha256,
                cancellationToken: cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) certificate.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManagedQuicBulkTransportListener(ManagedQuicSessionListener listener)
        : INativeBulkTransportListener
    {
        public IPEndPoint LocalEndPoint => listener.LocalEndPoint;

        public async ValueTask<IReliableRemoteSessionTransport> AcceptAsync(
            CancellationToken cancellationToken = default) =>
            await listener.AcceptAsync(cancellationToken);

        public ValueTask DisposeAsync() => listener.DisposeAsync();
    }
}
