using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerOnQ.Application.Security;

public static class SecureSessionProtocol
{
    public const int ProtocolVersion = 1;
    public const string HandshakeSuite = "HYBRID_MLKEM768_X25519";
    public const string IdentitySuite = "HYBRID_MLDSA65_ED25519";
    public const string TrafficProtection = "AES_256_GCM";
    public const string KeyDerivation = "HKDF_SHA512";
    public const int MaximumHandshakeFrameBytes = 64 * 1024;

    public static readonly byte[] ServerHelloSignatureContext =
        "PeerOnQ server hello v1"u8.ToArray();
    public static readonly byte[] ClientKeySignatureContext =
        "PeerOnQ client key v1"u8.ToArray();
}

public enum SecureHandshakeMessageType : byte
{
    ClientHello = 1,
    ServerHello = 2,
    ClientKey = 3,
    ServerFinish = 4,
    ClientFinish = 5,
    ServerAck = 6,
}

public abstract record SecureHandshakeMessage
{
    [JsonPropertyName("sessionId")] public required Guid SessionId { get; init; }
}

public sealed record SecureClientHello : SecureHandshakeMessage
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; init; } = SecureSessionProtocol.ProtocolVersion;
    [JsonPropertyName("handshakeSuite")] public string HandshakeSuite { get; init; } = SecureSessionProtocol.HandshakeSuite;
    [JsonPropertyName("identitySuite")] public string IdentitySuite { get; init; } = SecureSessionProtocol.IdentitySuite;
    [JsonPropertyName("trafficProtection")] public string TrafficProtection { get; init; } = SecureSessionProtocol.TrafficProtection;
    [JsonPropertyName("keyDerivation")] public string KeyDerivation { get; init; } = SecureSessionProtocol.KeyDerivation;
    [JsonPropertyName("nonce")] public required byte[] Nonce { get; init; }
    [JsonPropertyName("x25519PublicKey")] public required byte[] X25519PublicKey { get; init; }
    [JsonPropertyName("identity")] public required HybridIdentityPublic Identity { get; init; }
}

public sealed record SecureServerHello : SecureHandshakeMessage
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; init; } = SecureSessionProtocol.ProtocolVersion;
    [JsonPropertyName("handshakeSuite")] public string HandshakeSuite { get; init; } = SecureSessionProtocol.HandshakeSuite;
    [JsonPropertyName("identitySuite")] public string IdentitySuite { get; init; } = SecureSessionProtocol.IdentitySuite;
    [JsonPropertyName("trafficProtection")] public string TrafficProtection { get; init; } = SecureSessionProtocol.TrafficProtection;
    [JsonPropertyName("keyDerivation")] public string KeyDerivation { get; init; } = SecureSessionProtocol.KeyDerivation;
    [JsonPropertyName("nonce")] public required byte[] Nonce { get; init; }
    [JsonPropertyName("x25519PublicKey")] public required byte[] X25519PublicKey { get; init; }
    [JsonPropertyName("mlKem768PublicKey")] public required byte[] MlKem768PublicKey { get; init; }
    [JsonPropertyName("identity")] public required HybridIdentityPublic Identity { get; init; }
    [JsonPropertyName("ed25519Signature")] public required byte[] Ed25519Signature { get; init; }
    [JsonPropertyName("mlDsa65Signature")] public required byte[] MLDsa65Signature { get; init; }
}

public sealed record SecureClientKey : SecureHandshakeMessage
{
    [JsonPropertyName("mlKem768Ciphertext")] public required byte[] MlKem768Ciphertext { get; init; }
    [JsonPropertyName("ed25519Signature")] public required byte[] Ed25519Signature { get; init; }
    [JsonPropertyName("mlDsa65Signature")] public required byte[] MLDsa65Signature { get; init; }
    [JsonPropertyName("confirmation")] public required byte[] Confirmation { get; init; }
}

public sealed record SecureServerFinish : SecureHandshakeMessage
{
    [JsonPropertyName("confirmation")] public required byte[] Confirmation { get; init; }
}

public sealed record SecureClientFinish : SecureHandshakeMessage
{
    [JsonPropertyName("confirmation")] public required byte[] Confirmation { get; init; }
}

public sealed record SecureServerAck : SecureHandshakeMessage
{
    [JsonPropertyName("confirmation")] public required byte[] Confirmation { get; init; }
}

public static class SecureHandshakeCodec
{
    private static readonly byte[] Magic = "PNQH"u8.ToArray();
    private const int HeaderBytes = 10;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static bool IsHandshakeFrame(ReadOnlySpan<byte> frame) =>
        frame.Length >= HeaderBytes && frame[..4].SequenceEqual(Magic);

    public static byte[] Encode(SecureHandshakeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.SessionId == Guid.Empty)
            throw new InvalidOperationException("Secure handshake messages require a session binding.");
        var (type, declaredType) = MessageType(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, declaredType, JsonOptions);
        if (payload.Length is 0 || payload.Length > SecureSessionProtocol.MaximumHandshakeFrameBytes - HeaderBytes)
            throw new InvalidOperationException("Secure handshake message exceeds the protocol limit.");
        var frame = new byte[HeaderBytes + payload.Length];
        Magic.CopyTo(frame, 0);
        frame[4] = SecureSessionProtocol.ProtocolVersion;
        frame[5] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(6, 4), payload.Length);
        payload.CopyTo(frame, HeaderBytes);
        return frame;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out SecureHandshakeMessage? message,
        out string? error)
    {
        message = null;
        error = null;
        if (frame.Length < HeaderBytes
            || frame.Length > SecureSessionProtocol.MaximumHandshakeFrameBytes
            || !frame[..4].SequenceEqual(Magic))
        {
            error = "invalid_handshake_frame";
            return false;
        }
        if (frame[4] != SecureSessionProtocol.ProtocolVersion)
        {
            error = "secure_protocol_downgrade";
            return false;
        }
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(6, 4));
        if (payloadLength <= 0 || frame.Length != HeaderBytes + payloadLength)
        {
            error = "invalid_handshake_length";
            return false;
        }
        try
        {
            message = (SecureHandshakeMessageType)frame[5] switch
            {
                SecureHandshakeMessageType.ClientHello => JsonSerializer.Deserialize<SecureClientHello>(frame[HeaderBytes..], JsonOptions),
                SecureHandshakeMessageType.ServerHello => JsonSerializer.Deserialize<SecureServerHello>(frame[HeaderBytes..], JsonOptions),
                SecureHandshakeMessageType.ClientKey => JsonSerializer.Deserialize<SecureClientKey>(frame[HeaderBytes..], JsonOptions),
                SecureHandshakeMessageType.ServerFinish => JsonSerializer.Deserialize<SecureServerFinish>(frame[HeaderBytes..], JsonOptions),
                SecureHandshakeMessageType.ClientFinish => JsonSerializer.Deserialize<SecureClientFinish>(frame[HeaderBytes..], JsonOptions),
                SecureHandshakeMessageType.ServerAck => JsonSerializer.Deserialize<SecureServerAck>(frame[HeaderBytes..], JsonOptions),
                _ => null,
            };
            if (message is null || message.SessionId == Guid.Empty)
            {
                error = "unsupported_handshake_message";
                message = null;
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            error = "malformed_handshake_message";
            message = null;
            return false;
        }
    }

    public static byte[] TranscriptHash(params ReadOnlyMemory<byte>[] messages)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        Span<byte> length = stackalloc byte[4];
        foreach (var message in messages)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, message.Length);
            hash.AppendData(length);
            hash.AppendData(message.Span);
        }
        return hash.GetHashAndReset();
    }

    public static void ValidateHello(SecureClientHello hello, Guid expectedSessionId)
    {
        ValidateNegotiation(
            hello.SessionId,
            expectedSessionId,
            hello.ProtocolVersion,
            hello.HandshakeSuite,
            hello.IdentitySuite,
            hello.TrafficProtection,
            hello.KeyDerivation,
            hello.Nonce,
            hello.X25519PublicKey,
            hello.Identity);
    }

    public static void ValidateHello(SecureServerHello hello, Guid expectedSessionId)
    {
        ValidateNegotiation(
            hello.SessionId,
            expectedSessionId,
            hello.ProtocolVersion,
            hello.HandshakeSuite,
            hello.IdentitySuite,
            hello.TrafficProtection,
            hello.KeyDerivation,
            hello.Nonce,
            hello.X25519PublicKey,
            hello.Identity);
        if (hello.MlKem768PublicKey.Length != PostQuantumCryptography.MlKem768EncapsulationKeyBytes)
            throw new CryptographicException("The ML-KEM-768 public key has an invalid length.");
    }

    private static void ValidateNegotiation(
        Guid sessionId,
        Guid expectedSessionId,
        int protocolVersion,
        string handshakeSuite,
        string identitySuite,
        string trafficProtection,
        string keyDerivation,
        byte[] nonce,
        byte[] x25519PublicKey,
        HybridIdentityPublic identity)
    {
        if (sessionId != expectedSessionId) throw new CryptographicException("Secure handshake session binding mismatch.");
        if (protocolVersion != SecureSessionProtocol.ProtocolVersion
            || !string.Equals(handshakeSuite, SecureSessionProtocol.HandshakeSuite, StringComparison.Ordinal)
            || !string.Equals(identitySuite, SecureSessionProtocol.IdentitySuite, StringComparison.Ordinal)
            || !string.Equals(trafficProtection, SecureSessionProtocol.TrafficProtection, StringComparison.Ordinal)
            || !string.Equals(keyDerivation, SecureSessionProtocol.KeyDerivation, StringComparison.Ordinal))
        {
            throw new CryptographicException("Secure protocol downgrade or suite mismatch.");
        }
        if (nonce.Length != 32 || x25519PublicKey.Length != 32)
            throw new CryptographicException("Secure handshake key material has an invalid length.");
        ArgumentNullException.ThrowIfNull(identity);
    }

    private static (SecureHandshakeMessageType Type, Type DeclaredType) MessageType(SecureHandshakeMessage message) =>
        message switch
        {
            SecureClientHello => (SecureHandshakeMessageType.ClientHello, typeof(SecureClientHello)),
            SecureServerHello => (SecureHandshakeMessageType.ServerHello, typeof(SecureServerHello)),
            SecureClientKey => (SecureHandshakeMessageType.ClientKey, typeof(SecureClientKey)),
            SecureServerFinish => (SecureHandshakeMessageType.ServerFinish, typeof(SecureServerFinish)),
            SecureClientFinish => (SecureHandshakeMessageType.ClientFinish, typeof(SecureClientFinish)),
            SecureServerAck => (SecureHandshakeMessageType.ServerAck, typeof(SecureServerAck)),
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };
}
