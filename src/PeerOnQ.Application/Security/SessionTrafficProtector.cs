using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Security;

public enum SecureChannelKind : byte
{
    Control = 1,
    Video = 2,
    Input = 3,
    Clipboard = 4,
    FileTransfer = 5,
    Telemetry = 6,
}

public sealed record SecureSessionInfo
{
    public required int ProtocolVersion { get; init; }
    public required string HandshakeSuite { get; init; }
    public required string IdentitySuite { get; init; }
    public required string TrafficProtection { get; init; }
    public required string KeyDerivation { get; init; }
    public required string PeerIdentityFingerprint { get; init; }
    public required DateTimeOffset EstablishedAt { get; init; }
    public required bool PeerAuthenticated { get; init; }
    public required bool PostQuantumProtected { get; init; }
    public required ConnectionPath ConnectionPath { get; init; }
    public bool DirectP2P => ConnectionPath is ConnectionPath.DirectLan or ConnectionPath.DirectInternet;
}

public interface ISessionTrafficProtector
{
    bool IsEstablished { get; }
    byte[] ProtectVideoFrame(ReadOnlySpan<byte> encodedFrame);
    bool TryUnprotectVideoFrame(ReadOnlySpan<byte> protectedFrame, out byte[] plaintext, out string? error);
}

/// <summary>
/// Per-session AEAD record layer. AES-256-GCM and HKDF-SHA-512 come from the .NET platform
/// cryptographic provider. Epoch and sequence are authenticated; a key/nonce pair is never reused.
/// </summary>
public sealed class SessionTrafficProtector : ISessionTrafficProtector, IDisposable
{
    public const long RekeyAfterBytes = 1L * 1024 * 1024 * 1024;
    public const ulong RekeyAfterRecords = 1_000_000;
    public static readonly TimeSpan RekeyAfterTime = TimeSpan.FromMinutes(30);

    private const int HeaderBytes = 38;
    private const int TagBytes = 16;
    private const int NonceBytes = 12;
    private const int DerivedStateBytes = 36;
    private static readonly byte[] Magic = "PNQE"u8.ToArray();

    private readonly byte[] _masterSecret;
    private readonly byte[] _sessionId;
    private readonly string _sendDirection;
    private readonly string _receiveDirection;
    private readonly Lock _stateGate = new();
    private readonly Dictionary<TrafficContext, TrafficState> _sendStates = [];
    private readonly Dictionary<TrafficContext, TrafficState> _receiveStates = [];
    private int _forcedGeneration;
    private int _disposed;

    public SessionTrafficProtector(ReadOnlySpan<byte> masterSecret, SessionId sessionId, SessionRole role)
    {
        if (masterSecret.Length != 64) throw new ArgumentException("The session master secret must be 64 bytes.", nameof(masterSecret));
        _masterSecret = masterSecret.ToArray();
        _sessionId = sessionId.Value.ToByteArray(bigEndian: true);
        _sendDirection = role == SessionRole.Viewer ? "viewer-to-sharer" : "sharer-to-viewer";
        _receiveDirection = role == SessionRole.Viewer ? "sharer-to-viewer" : "viewer-to-sharer";
    }

    public bool IsEstablished => Volatile.Read(ref _disposed) == 0;

    public byte[] ProtectVideoFrame(ReadOnlySpan<byte> encodedFrame) =>
        Protect(SecureChannelKind.Video, Guid.Empty, encodedFrame);

    public bool TryUnprotectVideoFrame(
        ReadOnlySpan<byte> protectedFrame,
        out byte[] plaintext,
        out string? error) =>
        TryUnprotect(SecureChannelKind.Video, Guid.Empty, protectedFrame, out plaintext, out error);

    public bool TryUnprotect(
        ReadOnlySpan<byte> frame,
        out SecureChannelKind channel,
        out Guid transferId,
        out byte[] plaintext,
        out string? error)
    {
        channel = default;
        transferId = Guid.Empty;
        plaintext = [];
        error = null;
        if (!TryReadHeader(frame, out channel, out _, out _, out transferId, out _)
            || !IsValidContext(channel, transferId))
        {
            error = "invalid_secure_frame";
            return false;
        }

        return TryUnprotect(channel, transferId, frame, out plaintext, out error);
    }

    /// <summary>
    /// Reads the unauthenticated record context only to select an isolated receive lane. Callers
    /// must still use <c>TryUnprotect</c> before trusting the channel or transfer identifier.
    /// </summary>
    internal static bool TryReadRoutingContext(
        ReadOnlySpan<byte> frame,
        out SecureChannelKind channel,
        out Guid transferId) =>
        TryReadHeader(frame, out channel, out _, out _, out transferId, out _)
        && IsValidContext(channel, transferId);

    public byte[] Protect(
        SecureChannelKind channel,
        Guid transferId,
        ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ValidateContext(channel, transferId);
        if (plaintext.IsEmpty) throw new ArgumentException("Plaintext cannot be empty.", nameof(plaintext));

        var context = new TrafficContext(channel, transferId);
        while (true)
        {
            var state = GetOrCreateState(_sendStates, context, _sendDirection, epoch: 0);
            lock (state.Gate)
            {
                if (!ReferenceEquals(state, GetCurrentState(_sendStates, context))) continue;
                var forced = Volatile.Read(ref _forcedGeneration);
                if (state.ForcedGeneration != forced
                    || state.BytesProcessed >= RekeyAfterBytes
                    || state.NextSequence >= RekeyAfterRecords
                    || DateTimeOffset.UtcNow - state.CreatedAt >= RekeyAfterTime)
                {
                    RotateSendState(context, state, forced);
                    continue;
                }

                var sequence = state.NextSequence++;
                var frame = new byte[HeaderBytes + plaintext.Length + TagBytes];
                WriteHeader(frame, channel, state.Epoch, sequence, transferId, plaintext.Length);
                var nonce = BuildNonce(state.NoncePrefix, sequence);
                var associatedData = BuildAssociatedData(frame.AsSpan(0, HeaderBytes));
                try
                {
                    state.Cipher.Encrypt(
                        nonce,
                        plaintext,
                        frame.AsSpan(HeaderBytes, plaintext.Length),
                        frame.AsSpan(HeaderBytes + plaintext.Length, TagBytes),
                        associatedData);
                    state.BytesProcessed = checked(state.BytesProcessed + plaintext.Length);
                    return frame;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(associatedData);
                }
            }
        }
    }

    public bool TryUnprotect(
        SecureChannelKind expectedChannel,
        Guid expectedTransferId,
        ReadOnlySpan<byte> frame,
        out byte[] plaintext,
        out string? error)
    {
        plaintext = [];
        error = null;
        if (_disposed != 0)
        {
            error = "secure_context_closed";
            return false;
        }
        if (!IsValidContext(expectedChannel, expectedTransferId))
        {
            error = "invalid_secure_context";
            return false;
        }
        if (!TryReadHeader(frame, out var channel, out var epoch, out var sequence, out var transferId, out var length)
            || channel != expectedChannel
            || transferId != expectedTransferId)
        {
            error = "invalid_secure_frame";
            return false;
        }

        var context = new TrafficContext(channel, transferId);
        var state = GetOrCreateState(_receiveStates, context, _receiveDirection, epoch);
        lock (state.Gate)
        {
            var current = GetCurrentState(_receiveStates, context);
            if (!ReferenceEquals(state, current)) state = current;
            if (epoch < state.Epoch)
            {
                error = channel == SecureChannelKind.Video
                    ? "stale_video_record"
                    : "invalid_key_epoch";
                return false;
            }
            if (epoch > state.Epoch + 1)
            {
                error = "invalid_key_epoch";
                return false;
            }

            var candidate = state;
            var replacesCurrent = epoch == state.Epoch + 1;
            if (replacesCurrent)
                candidate = CreateState(context, _receiveDirection, epoch, Volatile.Read(ref _forcedGeneration));

            lock (candidate.Gate)
            {
                if (candidate.LastReceivedSequence is { } last
                    && (channel == SecureChannelKind.Video ? sequence <= last : sequence != last + 1))
                {
                    if (replacesCurrent) candidate.Dispose();
                    error = channel == SecureChannelKind.Video
                        ? "stale_video_record"
                        : "replayed_or_out_of_order_record";
                    return false;
                }
                // RTP video is lossy: the first authenticated frame that reaches this receiver
                // does not have to be the sender's sequence zero. Ordered collaboration channels
                // still require an exact zero-based sequence from their first record.
                if (candidate.LastReceivedSequence is null
                    && channel != SecureChannelKind.Video
                    && sequence != 0)
                {
                    if (replacesCurrent) candidate.Dispose();
                    error = "invalid_initial_sequence";
                    return false;
                }

                plaintext = new byte[length];
                var nonce = BuildNonce(candidate.NoncePrefix, sequence);
                var associatedData = BuildAssociatedData(frame[..HeaderBytes]);
                try
                {
                    candidate.Cipher.Decrypt(
                        nonce,
                        frame.Slice(HeaderBytes, length),
                        frame.Slice(HeaderBytes + length, TagBytes),
                        plaintext,
                        associatedData);
                    candidate.LastReceivedSequence = sequence;
                    candidate.BytesProcessed = checked(candidate.BytesProcessed + length);
                    if (replacesCurrent) ReplaceReceiveState(context, state, candidate);
                    return true;
                }
                catch (AuthenticationTagMismatchException)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    plaintext = [];
                    if (replacesCurrent) candidate.Dispose();
                    error = "authentication_failed";
                    return false;
                }
                catch (CryptographicException)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    plaintext = [];
                    if (replacesCurrent) candidate.Dispose();
                    error = "authentication_failed";
                    return false;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(associatedData);
                }
            }
        }
    }

    public void ForceRekey() => Interlocked.Increment(ref _forcedGeneration);

    public void ForgetTransfer(Guid transferId)
    {
        if (transferId == Guid.Empty) return;
        var context = new TrafficContext(SecureChannelKind.FileTransfer, transferId);
        lock (_stateGate)
        {
            if (_sendStates.Remove(context, out var send)) send.Dispose();
            if (_receiveStates.Remove(context, out var receive)) receive.Dispose();
        }
    }

    private TrafficState RotateSendState(TrafficContext context, TrafficState previous, int forced)
    {
        var replacement = CreateState(context, _sendDirection, checked(previous.Epoch + 1), forced);
        lock (_stateGate)
        {
            _sendStates[context] = replacement;
        }
        previous.Dispose();
        return replacement;
    }

    private void ReplaceReceiveState(TrafficContext context, TrafficState previous, TrafficState replacement)
    {
        lock (_stateGate)
        {
            _receiveStates[context] = replacement;
        }
        previous.Dispose();
    }

    private TrafficState GetOrCreateState(
        Dictionary<TrafficContext, TrafficState> states,
        TrafficContext context,
        string direction,
        uint epoch)
    {
        lock (_stateGate)
        {
            if (states.TryGetValue(context, out var state)) return state;
            state = CreateState(context, direction, epoch, Volatile.Read(ref _forcedGeneration));
            states.Add(context, state);
            return state;
        }
    }

    private TrafficState GetCurrentState(Dictionary<TrafficContext, TrafficState> states, TrafficContext context)
    {
        lock (_stateGate) return states[context];
    }

    private TrafficState CreateState(TrafficContext context, string direction, uint epoch, int forced)
    {
        var info = Encoding.UTF8.GetBytes(
            $"PeerOnQ traffic v1|{direction}|{context.Channel}|{context.TransferId:N}|{epoch}");
        var material = new byte[DerivedStateBytes];
        HKDF.Expand(HashAlgorithmName.SHA512, _masterSecret, material, info);
        try
        {
            return new TrafficState(material[..32], material[32..], epoch, forced);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private byte[] BuildAssociatedData(ReadOnlySpan<byte> header)
    {
        var associatedData = new byte[header.Length + _sessionId.Length];
        header.CopyTo(associatedData);
        _sessionId.CopyTo(associatedData, header.Length);
        return associatedData;
    }

    private static byte[] BuildNonce(ReadOnlySpan<byte> prefix, ulong sequence)
    {
        var nonce = new byte[NonceBytes];
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), sequence);
        return nonce;
    }

    private static void WriteHeader(
        Span<byte> destination,
        SecureChannelKind channel,
        uint epoch,
        ulong sequence,
        Guid transferId,
        int ciphertextLength)
    {
        Magic.CopyTo(destination);
        destination[4] = SecureSessionProtocol.ProtocolVersion;
        destination[5] = (byte)channel;
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(6, 4), epoch);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(10, 8), sequence);
        transferId.TryWriteBytes(destination.Slice(18, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(34, 4), ciphertextLength);
    }

    private static bool TryReadHeader(
        ReadOnlySpan<byte> frame,
        out SecureChannelKind channel,
        out uint epoch,
        out ulong sequence,
        out Guid transferId,
        out int ciphertextLength)
    {
        channel = default;
        epoch = 0;
        sequence = 0;
        transferId = Guid.Empty;
        ciphertextLength = 0;
        if (frame.Length < HeaderBytes + TagBytes
            || !frame[..4].SequenceEqual(Magic)
            || frame[4] != SecureSessionProtocol.ProtocolVersion
            || !Enum.IsDefined((SecureChannelKind)frame[5])) return false;
        channel = (SecureChannelKind)frame[5];
        epoch = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(6, 4));
        sequence = BinaryPrimitives.ReadUInt64BigEndian(frame.Slice(10, 8));
        transferId = new Guid(frame.Slice(18, 16), bigEndian: true);
        ciphertextLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(34, 4));
        return ciphertextLength > 0 && frame.Length == HeaderBytes + ciphertextLength + TagBytes;
    }

    private static void ValidateContext(SecureChannelKind channel, Guid transferId)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        if (!IsValidContext(channel, transferId))
            throw new ArgumentException("File-transfer records require a unique transfer ID and other channels forbid it.");
    }

    private static bool IsValidContext(SecureChannelKind channel, Guid transferId) =>
        Enum.IsDefined(channel)
        && (channel == SecureChannelKind.FileTransfer) == (transferId != Guid.Empty);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_stateGate)
        {
            foreach (var state in _sendStates.Values) state.Dispose();
            foreach (var state in _receiveStates.Values) state.Dispose();
            _sendStates.Clear();
            _receiveStates.Clear();
        }
        CryptographicOperations.ZeroMemory(_masterSecret);
    }

    private readonly record struct TrafficContext(SecureChannelKind Channel, Guid TransferId);

    private sealed class TrafficState(byte[] key, byte[] noncePrefix, uint epoch, int forcedGeneration) : IDisposable
    {
        public object Gate { get; } = new();
        public byte[] Key { get; } = key;
        public byte[] NoncePrefix { get; } = noncePrefix;
        public AesGcm Cipher { get; } = new(key, TagBytes);
        public uint Epoch { get; } = epoch;
        public int ForcedGeneration { get; } = forcedGeneration;
        public ulong NextSequence { get; set; }
        public ulong? LastReceivedSequence { get; set; }
        public long BytesProcessed { get; set; }
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cipher.Dispose();
            CryptographicOperations.ZeroMemory(Key);
            CryptographicOperations.ZeroMemory(NoncePrefix);
        }
    }
}

public static class SecureSessionKeySchedule
{
    public static byte[] DeriveMasterSecret(
        SessionId sessionId,
        ReadOnlySpan<byte> pqSecret,
        ReadOnlySpan<byte> classicalSecret,
        ReadOnlySpan<byte> transcriptHash)
    {
        if (pqSecret.Length != 32 || classicalSecret.Length != 32 || transcriptHash.Length != 64)
            throw new CryptographicException("The hybrid key schedule input has an invalid length.");
        var input = new byte[pqSecret.Length + classicalSecret.Length];
        pqSecret.CopyTo(input);
        classicalSecret.CopyTo(input.AsSpan(pqSecret.Length));
        var info = Encoding.UTF8.GetBytes(
            $"PeerOnQ Secure Transport v1|{sessionId.Value:N}|{SecureSessionProtocol.HandshakeSuite}");
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA512, input, 64, transcriptHash.ToArray(), info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public static byte[] DeriveAuthenticationKey(ReadOnlySpan<byte> masterSecret, string direction)
    {
        if (masterSecret.Length != 64) throw new ArgumentException("Master secret must be 64 bytes.", nameof(masterSecret));
        var output = new byte[64];
        HKDF.Expand(
            HashAlgorithmName.SHA512,
            masterSecret,
            output,
            Encoding.UTF8.GetBytes($"PeerOnQ authentication v1|{direction}"));
        return output;
    }
}
