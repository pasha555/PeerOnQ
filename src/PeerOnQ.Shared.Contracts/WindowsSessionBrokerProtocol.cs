using System.Buffers.Binary;

namespace PeerOnQ.Shared.Contracts;

/// <summary>Local-only session registration. Contains no credentials or remote-control commands.</summary>
public static class WindowsSessionBrokerProtocol
{
    public const string ServiceName = "PeerOnQ.SessionBroker";
    public const string PipeName = "PeerOnQ.SessionBroker.v1";
    public const string RegistryKey = @"Software\PeerOnQ\SessionBroker";
    public const int PacketSize = 8;
    public const int HeartbeatSeconds = 10;
    public const int LeaseSeconds = 30;

    public static byte[] Request(SessionBrokerOperation operation)
    {
        var packet = new byte[PacketSize];
        "PQS1"u8.CopyTo(packet);
        packet[4] = (byte)operation;
        return packet;
    }

    public static bool TryReadRequest(ReadOnlySpan<byte> packet, out SessionBrokerOperation operation)
    {
        operation = default;
        if (packet.Length != PacketSize || !packet[..4].SequenceEqual("PQS1"u8) ||
            packet[5] != 0 || packet[6] != 0 || packet[7] != 0 || packet[4] is < 1 or > 3)
            return false;
        operation = (SessionBrokerOperation)packet[4];
        return true;
    }

    public static byte[] Response(bool accepted)
    {
        var packet = new byte[PacketSize];
        "PQR1"u8.CopyTo(packet);
        packet[4] = accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), LeaseSeconds);
        return packet;
    }

    public static bool IsAccepted(ReadOnlySpan<byte> packet) =>
        packet.Length == PacketSize && packet[..4].SequenceEqual("PQR1"u8) &&
        packet[4] == 1 && packet[5] == 0 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]) == LeaseSeconds;
}

public enum SessionBrokerOperation : byte
{
    Register = 1,
    Heartbeat = 2,
    Unregister = 3
}
