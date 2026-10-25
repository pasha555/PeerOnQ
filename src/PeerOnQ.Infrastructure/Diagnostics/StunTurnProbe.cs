using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PeerOnQ.Infrastructure.Diagnostics;

/// <summary>How a relay (TURN) probe reaches the server.</summary>
public enum RelayProbeTransport
{
    Udp = 0,
    Tcp = 1,
    Tls = 2,
}

public enum StunProbeResultKind
{
    /// <summary>A STUN Binding success response that carried a mapped address.</summary>
    BindingSucceeded = 0,

    /// <summary>The relay allocated without credentials (and the allocation was released).</summary>
    AllocationSucceeded = 1,

    /// <summary>401 with REALM and NONCE: the relay service works and wants session credentials.</summary>
    AuthenticationRequired = 2,

    /// <summary>A well-formed STUN error response other than the expected 401 challenge.</summary>
    ErrorResponse = 3,

    /// <summary>Something answered, but not with a valid response to this request.</summary>
    InvalidResponse = 4,
}

/// <summary>The outcome of one bounded STUN/TURN exchange. Never carries an address.</summary>
public sealed record StunProbeOutcome(StunProbeResultKind Kind, double RoundTripMs, int ErrorCode = 0);

/// <summary>
/// Minimal RFC 5389 / RFC 5766 client used by Network Doctor. A Binding request proves STUN; an
/// unauthenticated Allocate proves the relay service on UDP, TCP and TLS: coturn answers it with
/// 401 + REALM + NONCE. No TURN credential is ever sent, so no relay allocation is created.
/// Mapped/relayed addresses are validated but never returned or logged.
/// </summary>
public static class StunTurnProbe
{
    private static readonly TimeSpan InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(500);

    public static async Task<StunProbeOutcome> BindAsync(IPEndPoint server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var transactionId = StunMessage.NewTransactionId();
        var request = StunMessage.CreateBindingRequest(transactionId);
        var (response, rtt) = await ExchangeUdpAsync(server, request, transactionId, StunMessage.BindingMethod, cancellationToken);
        return ToOutcome(response, rtt, StunMessage.BindingMethod);
    }

    public static async Task<StunProbeOutcome> AllocateAsync(
        IPEndPoint server,
        RelayProbeTransport transport,
        string serverName,
        RemoteCertificateValidationCallback? certificateValidation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var transactionId = StunMessage.NewTransactionId();
        var request = StunMessage.CreateAllocateRequest(transactionId);
        if (transport == RelayProbeTransport.Udp)
        {
            var (udpResponse, udpRtt) = await ExchangeUdpAsync(
                server, request, transactionId, StunMessage.AllocateMethod, cancellationToken);
            if (udpResponse.Kind == StunResponseKind.Success)
                await ReleaseUdpAllocationAsync(server, cancellationToken);
            return ToOutcome(udpResponse, udpRtt, StunMessage.AllocateMethod);
        }

        using var client = new TcpClient(server.AddressFamily);
        await client.ConnectAsync(server, cancellationToken);
        Stream stream = client.GetStream();
        SslStream? tls = null;
        try
        {
            if (transport == RelayProbeTransport.Tls)
            {
                // Normal certificate validation against the relay's DNS name unless a test
                // supplies its own pinned callback.
                tls = new SslStream(stream, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = serverName,
                    RemoteCertificateValidationCallback = certificateValidation,
                }, cancellationToken);
                stream = tls;
            }

            // A TCP/TLS allocation, if one was ever granted, ends when this connection closes.
            var (response, rtt) = await ExchangeStreamAsync(
                stream, request, transactionId, StunMessage.AllocateMethod, cancellationToken);
            return ToOutcome(response, rtt, StunMessage.AllocateMethod);
        }
        finally
        {
            if (tls is not null) await tls.DisposeAsync();
        }
    }

    private static StunProbeOutcome ToOutcome(StunResponse response, double rtt, ushort method) => response.Kind switch
    {
        StunResponseKind.Success when method == StunMessage.BindingMethod && response.HasMappedAddress =>
            new(StunProbeResultKind.BindingSucceeded, rtt),
        StunResponseKind.Success when method == StunMessage.AllocateMethod =>
            new(StunProbeResultKind.AllocationSucceeded, rtt),
        StunResponseKind.Error when response.ErrorCode == 401 && response.HasRealm && response.HasNonce =>
            new(StunProbeResultKind.AuthenticationRequired, rtt, 401),
        StunResponseKind.Error => new(StunProbeResultKind.ErrorResponse, rtt, response.ErrorCode),
        _ => new(StunProbeResultKind.InvalidResponse, rtt),
    };

    private static async Task<(StunResponse Response, double RoundTripMs)> ExchangeUdpAsync(
        IPEndPoint server,
        byte[] request,
        byte[] transactionId,
        ushort method,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(server);
        var buffer = new byte[StunMessage.MaximumMessageBytes];
        var retransmission = InitialRetransmissionTimeout;
        double? undecodableAfterMs = null;
        while (true)
        {
            var sentAt = Stopwatch.GetTimestamp();
            await socket.SendAsync(request, SocketFlags.None, cancellationToken);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(retransmission);
            try
            {
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer, SocketFlags.None, wait.Token);
                    var elapsed = Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
                    if (StunMessage.TryParseResponse(buffer.AsSpan(0, received), transactionId, method, out var response))
                        return (response, elapsed);
                    // UDP cannot tell a broken server from a stray datagram: keep listening, but
                    // report an invalid reply instead of a silent timeout if nothing valid follows.
                    undecodableAfterMs ??= elapsed;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                retransmission += retransmission;
            }
            catch (OperationCanceledException) when (undecodableAfterMs is { } firstInvalid)
            {
                return (StunResponse.Invalid, firstInvalid);
            }
        }
    }

    private static async Task<(StunResponse Response, double RoundTripMs)> ExchangeStreamAsync(
        Stream stream,
        byte[] request,
        byte[] transactionId,
        ushort method,
        CancellationToken cancellationToken)
    {
        var sentAt = Stopwatch.GetTimestamp();
        await stream.WriteAsync(request, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        var header = new byte[StunMessage.HeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if ((header[0] & 0xC0) != 0 || length > StunMessage.MaximumMessageBytes - StunMessage.HeaderBytes)
            return (StunResponse.Invalid, Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds);

        var message = new byte[StunMessage.HeaderBytes + length];
        header.CopyTo(message, 0);
        await stream.ReadExactlyAsync(message.AsMemory(StunMessage.HeaderBytes), cancellationToken);
        var rtt = Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
        return StunMessage.TryParseResponse(message, transactionId, method, out var response)
            ? (response, rtt)
            : (StunResponse.Invalid, rtt);
    }

    /// <summary>A server that allocated without credentials gets an immediate lifetime-0 refresh.</summary>
    private static async Task ReleaseUdpAllocationAsync(IPEndPoint server, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(server);
            await socket.SendAsync(
                StunMessage.CreateRefreshRequest(StunMessage.NewTransactionId(), lifetimeSeconds: 0),
                SocketFlags.None,
                cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // Best effort: the allocation also expires on its own lifetime.
        }
    }
}

internal enum StunResponseKind
{
    Success,
    Error,
    Invalid,
}

internal sealed record StunResponse(
    StunResponseKind Kind,
    int ErrorCode = 0,
    bool HasMappedAddress = false,
    bool HasRealm = false,
    bool HasNonce = false)
{
    public static StunResponse Invalid { get; } = new(StunResponseKind.Invalid);
}

/// <summary>RFC 5389 message framing for the few requests Network Doctor sends.</summary>
internal static class StunMessage
{
    public const int HeaderBytes = 20;
    public const int MaximumMessageBytes = 1500;
    public const uint MagicCookie = 0x2112A442;
    public const ushort BindingMethod = 0x001;
    public const ushort AllocateMethod = 0x003;
    public const ushort RefreshMethod = 0x004;

    public const ushort MappedAddressAttribute = 0x0001;
    public const ushort ErrorCodeAttribute = 0x0009;
    public const ushort LifetimeAttribute = 0x000D;
    public const ushort RealmAttribute = 0x0014;
    public const ushort NonceAttribute = 0x0015;
    public const ushort RequestedTransportAttribute = 0x0019;
    public const ushort XorMappedAddressAttribute = 0x0020;

    private const byte UdpProtocolNumber = 17;

    public static byte[] NewTransactionId() => RandomNumberGenerator.GetBytes(12);

    public static byte[] CreateBindingRequest(ReadOnlySpan<byte> transactionId) =>
        Create(MessageType(BindingMethod, MessageClass.Request), transactionId, []);

    /// <summary>Unauthenticated Allocate for a UDP relay (REQUESTED-TRANSPORT = 17).</summary>
    public static byte[] CreateAllocateRequest(ReadOnlySpan<byte> transactionId) =>
        Create(MessageType(AllocateMethod, MessageClass.Request), transactionId,
            [(RequestedTransportAttribute, [UdpProtocolNumber, 0, 0, 0])]);

    public static byte[] CreateRefreshRequest(ReadOnlySpan<byte> transactionId, uint lifetimeSeconds)
    {
        var lifetime = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lifetime, lifetimeSeconds);
        return Create(MessageType(RefreshMethod, MessageClass.Request), transactionId, [(LifetimeAttribute, lifetime)]);
    }

    public static byte[] Create(
        ushort messageType,
        ReadOnlySpan<byte> transactionId,
        IReadOnlyList<(ushort Type, byte[] Value)> attributes)
    {
        if (transactionId.Length != 12) throw new ArgumentException("A STUN transaction ID has 12 bytes.", nameof(transactionId));
        var bodyLength = attributes.Sum(attribute => 4 + Padded(attribute.Value.Length));
        var message = new byte[HeaderBytes + bodyLength];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0), messageType);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), checked((ushort)bodyLength));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), MagicCookie);
        transactionId.CopyTo(message.AsSpan(8));
        var offset = HeaderBytes;
        foreach (var (type, value) in attributes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset), type);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset + 2), checked((ushort)value.Length));
            value.CopyTo(message, offset + 4);
            offset += 4 + Padded(value.Length);
        }
        return message;
    }

    internal enum MessageClass
    {
        Request = 0,
        Indication = 1,
        SuccessResponse = 2,
        ErrorResponse = 3,
    }

    /// <summary>Interleaves the 12-bit method with the two class bits (RFC 5389 section 6).</summary>
    public static ushort MessageType(ushort method, MessageClass messageClass)
    {
        var c = (int)messageClass;
        return (ushort)((method & 0x000F)
                        | ((method & 0x0070) << 1)
                        | ((method & 0x0F80) << 2)
                        | ((c & 0x1) << 4)
                        | ((c & 0x2) << 7));
    }

    private static (ushort Method, MessageClass Class) SplitType(ushort type) => (
        (ushort)((type & 0x000F) | ((type & 0x00E0) >> 1) | ((type & 0x3E00) >> 2)),
        (MessageClass)(((type & 0x0010) >> 4) | ((type & 0x0100) >> 7)));

    /// <summary>
    /// Accepts only a well-formed success/error response to <paramref name="expectedMethod"/> with
    /// the expected cookie and transaction ID. Attribute bounds are checked; addresses are not kept.
    /// </summary>
    public static bool TryParseResponse(
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> expectedTransactionId,
        ushort expectedMethod,
        out StunResponse response)
    {
        response = StunResponse.Invalid;
        if (message.Length < HeaderBytes || (message[0] & 0xC0) != 0) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if (length % 4 != 0 || HeaderBytes + length != message.Length) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(message[4..]) != MagicCookie) return false;
        if (!message.Slice(8, 12).SequenceEqual(expectedTransactionId)) return false;

        var (method, messageClass) = SplitType(BinaryPrimitives.ReadUInt16BigEndian(message));
        if (method != expectedMethod
            || messageClass is not (MessageClass.SuccessResponse or MessageClass.ErrorResponse))
            return false;

        var errorCode = 0;
        bool mapped = false, realm = false, nonce = false;
        var offset = HeaderBytes;
        while (offset < message.Length)
        {
            if (offset + 4 > message.Length) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..]);
            var valueStart = offset + 4;
            if (valueStart + valueLength > message.Length) return false;
            var value = message.Slice(valueStart, valueLength);
            switch (type)
            {
                case ErrorCodeAttribute:
                    if (valueLength < 4) return false;
                    var errorClass = value[2] & 0x07;
                    var number = value[3];
                    if (errorClass is < 3 or > 6 || number > 99) return false;
                    errorCode = errorClass * 100 + number;
                    break;
                case XorMappedAddressAttribute or MappedAddressAttribute:
                    if (!IsValidAddressValue(value)) return false;
                    mapped = true;
                    break;
                case RealmAttribute:
                    realm = valueLength > 0;
                    break;
                case NonceAttribute:
                    nonce = valueLength > 0;
                    break;
            }
            offset = valueStart + Padded(valueLength);
        }

        if (messageClass == MessageClass.ErrorResponse)
        {
            if (errorCode == 0) return false;
            response = new StunResponse(StunResponseKind.Error, errorCode, mapped, realm, nonce);
            return true;
        }

        response = new StunResponse(StunResponseKind.Success, 0, mapped, realm, nonce);
        return true;
    }

    private static bool IsValidAddressValue(ReadOnlySpan<byte> value) =>
        value.Length >= 4 && value[1] switch
        {
            0x01 => value.Length == 8,
            0x02 => value.Length == 20,
            _ => false,
        };

    private static int Padded(int length) => (length + 3) & ~3;
}
