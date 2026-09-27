using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerOnQ.Transport.DataPlane;

/// <summary>
/// Certificate pin used to bind the TLS 1.3 transport to the authenticated signaling session.
/// The pin is SHA-256 over the peer certificate DER and must arrive over an authenticated channel.
/// </summary>
public sealed class QuicPeerAuthentication
{
    private readonly byte[] _expectedCertificateHash;

    public QuicPeerAuthentication(string expectedCertificateSha256)
    {
        _expectedCertificateHash = ParseFingerprint(expectedCertificateSha256);
    }

    public static string GetCertificateSha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
    }

    internal static void ValidateLocalCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
            throw new ArgumentException("The local QUIC certificate must include its private key.", nameof(certificate));

        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            throw new ArgumentException("The local QUIC certificate is outside its validity period.", nameof(certificate));
    }

    public bool Validate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        _ = sender;
        _ = chain;
        _ = sslPolicyErrors;
        if (certificate is null) return false;

        using var peer = new X509Certificate2(certificate);
        var now = DateTime.UtcNow;
        if (now < peer.NotBefore.ToUniversalTime() || now > peer.NotAfter.ToUniversalTime())
            return false;

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        return SHA256.TryHashData(peer.RawData, actual, out var written)
               && written == SHA256.HashSizeInBytes
               && CryptographicOperations.FixedTimeEquals(actual, _expectedCertificateHash);
    }

    private static byte[] ParseFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The expected peer certificate fingerprint is required.", nameof(value));

        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized.Length != SHA256.HashSizeInBytes * 2)
            throw new ArgumentException("The certificate fingerprint must contain one SHA-256 digest.", nameof(value));

        try
        {
            return Convert.FromHexString(normalized);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The certificate fingerprint is not hexadecimal.", nameof(value), ex);
        }
    }
}
