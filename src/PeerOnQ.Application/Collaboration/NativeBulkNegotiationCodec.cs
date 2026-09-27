using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PeerOnQ.Application.Collaboration;

/// <summary>
/// Bounded native-bulk negotiation carried only inside the authenticated PNQE telemetry channel.
/// The selected WebRTC peer address is reused locally, so no address is serialized or logged.
/// </summary>
internal static class NativeBulkNegotiationCodec
{
    private const uint Magic = 0x504E514E; // PNQN
    private const byte Version = 1;
    private const int HeaderBytes = 8;
    private const int FingerprintBytes = SHA256.HashSizeInBytes;
    private const int ClientHelloBytes = HeaderBytes + FingerprintBytes;
    private const int ServerOfferBytes = ClientHelloBytes + sizeof(ushort);

    private enum MessageType : byte
    {
        ClientHello = 1,
        ServerOffer = 2,
    }

    public static bool IsFrame(ReadOnlySpan<byte> payload) =>
        payload.Length >= HeaderBytes && BinaryPrimitives.ReadUInt32BigEndian(payload) == Magic;

    public static byte[] EncodeClientHello(string certificateSha256) =>
        Encode(MessageType.ClientHello, certificateSha256, port: null);

    public static byte[] EncodeServerOffer(string certificateSha256, int port)
    {
        if (port is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port));
        return Encode(MessageType.ServerOffer, certificateSha256, checked((ushort)port));
    }

    public static bool TryDecodeClientHello(ReadOnlySpan<byte> payload, out string certificateSha256)
    {
        certificateSha256 = string.Empty;
        return TryDecode(payload, MessageType.ClientHello, ClientHelloBytes, out certificateSha256, out _);
    }

    public static bool TryDecodeServerOffer(
        ReadOnlySpan<byte> payload,
        out string certificateSha256,
        out int port)
    {
        certificateSha256 = string.Empty;
        port = 0;
        if (!TryDecode(payload, MessageType.ServerOffer, ServerOfferBytes, out certificateSha256, out var decodedPort)
            || decodedPort == 0)
        {
            certificateSha256 = string.Empty;
            return false;
        }
        port = decodedPort;
        return true;
    }

    private static byte[] Encode(MessageType type, string certificateSha256, ushort? port)
    {
        var fingerprint = ParseFingerprint(certificateSha256);
        var payload = new byte[port.HasValue ? ServerOfferBytes : ClientHelloBytes];
        BinaryPrimitives.WriteUInt32BigEndian(payload, Magic);
        payload[4] = Version;
        payload[5] = (byte)type;
        fingerprint.CopyTo(payload.AsSpan(HeaderBytes));
        if (port.HasValue)
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(ClientHelloBytes), port.Value);
        CryptographicOperations.ZeroMemory(fingerprint);
        return payload;
    }

    private static bool TryDecode(
        ReadOnlySpan<byte> payload,
        MessageType expectedType,
        int expectedLength,
        out string certificateSha256,
        out int port)
    {
        certificateSha256 = string.Empty;
        port = 0;
        if (payload.Length != expectedLength
            || BinaryPrimitives.ReadUInt32BigEndian(payload) != Magic
            || payload[4] != Version
            || payload[5] != (byte)expectedType
            || payload[6] != 0
            || payload[7] != 0)
        {
            return false;
        }

        certificateSha256 = Convert.ToHexStringLower(payload.Slice(HeaderBytes, FingerprintBytes));
        if (expectedType == MessageType.ServerOffer)
            port = BinaryPrimitives.ReadUInt16BigEndian(payload[ClientHelloBytes..]);
        return true;
    }

    private static byte[] ParseFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The native bulk certificate fingerprint is required.", nameof(value));
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized.Length != FingerprintBytes * 2)
            throw new ArgumentException("The native bulk certificate fingerprint must be SHA-256.", nameof(value));
        try
        {
            return Convert.FromHexString(normalized);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The native bulk certificate fingerprint is not hexadecimal.", nameof(value), ex);
        }
    }
}
