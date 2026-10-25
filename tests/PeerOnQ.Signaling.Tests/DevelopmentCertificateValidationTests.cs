using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PeerOnQ.Transport;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class DevelopmentCertificateValidationTests
{
    [Fact]
    public void Pinned_development_root_repairs_only_chain_trust_errors()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            "CN=PeerOnQ Test Root",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var root = rootRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest(
            "CN=signal.peeronq.com",
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        using var server = serverRequest.Create(
            root,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16));

        Assert.True(WebSocketSignalingClient.ValidateDevelopmentServerCertificate(
            server,
            SslPolicyErrors.RemoteCertificateChainErrors,
            root.RawData));
        Assert.True(WebSocketSignalingClient.ValidateDevelopmentServerCertificate(
            server,
            SslPolicyErrors.None,
            root.RawData));
        Assert.False(WebSocketSignalingClient.ValidateDevelopmentServerCertificate(
            server,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            root.RawData));

        using var unrelatedKey = RSA.Create(2048);
        var unrelatedRequest = new CertificateRequest(
            "CN=Unrelated Root",
            unrelatedKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        unrelatedRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var unrelatedRoot = unrelatedRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(WebSocketSignalingClient.ValidateDevelopmentServerCertificate(
            server,
            SslPolicyErrors.RemoteCertificateChainErrors,
            unrelatedRoot.RawData));
        Assert.False(WebSocketSignalingClient.ValidateDevelopmentServerCertificate(
            server,
            SslPolicyErrors.None,
            unrelatedRoot.RawData));
    }
}
