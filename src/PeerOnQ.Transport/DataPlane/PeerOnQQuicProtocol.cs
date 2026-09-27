using System.Buffers.Binary;
using System.Net.Security;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Transport.DataPlane;

/// <summary>Wire constants for the native PeerOnQ QUIC data plane.</summary>
public static class PeerOnQQuicProtocol
{
    public const int Version = 1;
    public const int StreamHeaderLength = 8;
    public const int MessageLengthPrefixLength = sizeof(int);
    public const long ProtocolErrorCode = 0x100;
    public const long AuthenticationErrorCode = 0x101;
    public const long ShutdownErrorCode = 0;

    private const uint StreamMagic = 0x504F4E51; // PONQ
    private const byte ParallelLaneFlag = 1;

    public static SslApplicationProtocol ApplicationProtocol { get; } = new("peeronq-dp/1");

    public static void WriteStreamHeader(
        Span<byte> destination,
        RemoteTransportChannel channel,
        bool parallelLane)
    {
        if (destination.Length < StreamHeaderLength)
            throw new ArgumentException("The stream header buffer is too small.", nameof(destination));

        var definition = RemoteTransportChannels.Get(channel);
        if (definition.Delivery != RemoteTransportDelivery.ReliableOrdered)
            throw new ArgumentException("QUIC streams may carry reliable channels only.", nameof(channel));
        if (parallelLane && !definition.AllowsParallelLanes)
            throw new ArgumentException("This channel does not allow parallel lanes.", nameof(parallelLane));

        BinaryPrimitives.WriteUInt32BigEndian(destination, StreamMagic);
        destination[4] = Version;
        destination[5] = (byte)channel;
        destination[6] = parallelLane ? ParallelLaneFlag : (byte)0;
        destination[7] = 0;
    }

    public static bool TryReadStreamHeader(
        ReadOnlySpan<byte> source,
        out RemoteTransportChannel channel,
        out bool parallelLane)
    {
        channel = default;
        parallelLane = false;
        if (source.Length < StreamHeaderLength
            || BinaryPrimitives.ReadUInt32BigEndian(source) != StreamMagic
            || source[4] != Version
            || source[7] != 0
            || (source[6] & ~ParallelLaneFlag) != 0
            || !Enum.IsDefined(typeof(RemoteTransportChannel), source[5]))
        {
            return false;
        }

        channel = (RemoteTransportChannel)source[5];
        parallelLane = (source[6] & ParallelLaneFlag) != 0;
        var definition = RemoteTransportChannels.Get(channel);
        return definition.Delivery == RemoteTransportDelivery.ReliableOrdered
               && (!parallelLane || definition.AllowsParallelLanes);
    }
}
