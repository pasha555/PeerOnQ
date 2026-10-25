using System.Net;
using System.Net.Sockets;
using System.Text;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

/// <summary>
/// Regression checks for the maintained SIPSorcery 8.0.14 derivative: a malformed or unexpected inbound datagram must
/// not close the media receive path, pre-authentication STUN parsing must not throw, SRTP replay protection must hold
/// for reordered packets, and an oversized datagram must not abort the DTLS handshake. Public SIPSorcery APIs only.
/// </summary>
public sealed class SipSorceryReceiveHardeningTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Failing_packet_handler_drops_the_packet_and_the_receiver_keeps_receiving()
    {
        using var socket = BoundSocket();
        using var sender = BoundSocket();
        var receiver = new UdpReceiver(socket);
        var closed = false;
        var delivered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnClosed += _ => closed = true;
        receiver.OnPacketReceived += (_, _, _, packet) =>
        {
            if (packet[0] == 0xff) throw new IndexOutOfRangeException("simulated malformed packet");
            delivered.TrySetResult(packet);
        };
        try
        {
            receiver.BeginReceiveFrom();
            sender.SendTo([0xff], socket.LocalEndPoint!);
            sender.SendTo([0x01, 0x02], socket.LocalEndPoint!);
            Assert.Equal(new byte[] { 0x01, 0x02 }, await delivered.Task.WaitAsync(Timeout));
            Assert.False(closed);
            Assert.False(receiver.IsClosed);
        }
        finally { receiver.Close("test completed"); }
    }

    [Fact]
    public async Task Icmp_port_unreachable_does_not_stop_the_receive_loop()
    {
        using var socket = BoundSocket();
        using var sender = BoundSocket();
        var receiver = new UdpReceiver(socket);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnPacketReceived += (_, _, _, packet) => { if (packet[0] == 0x42) delivered.TrySetResult(); };
        try
        {
            receiver.BeginReceiveFrom();
            EndPoint closedPort;
            using (var probe = BoundSocket()) closedPort = probe.LocalEndPoint!;
            socket.SendTo([0x00], closedPort);
            await Task.Delay(200);
            sender.SendTo([0x42], socket.LocalEndPoint!);
            await delivered.Task.WaitAsync(Timeout);
            Assert.False(receiver.IsClosed);
        }
        finally { receiver.Close("test completed"); }
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x08, 0x21, 0x12, 0xa4, 0x42, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x04, 0x21, 0x12, 0xa4, 0x42, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0x00, 0x20, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x01, 0x01, 0x00, 0x0c, 0x21, 0x12, 0xa4, 0x42, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0x00, 0x20, 0x00, 0x08, 0x00, 0x02, 0x12, 0x34, 1, 2, 3, 4 })]
    public void Malformed_stun_messages_do_not_throw(byte[] datagram)
    {
        var message = STUNMessage.ParseSTUNMessage(datagram, datagram.Length);
        message?.CheckIntegrity(Encoding.UTF8.GetBytes("0123456789abcdefghijklmn"));
    }

    [Fact]
    public void Reordered_srtp_packet_cannot_be_replayed()
    {
        var sender = SrtpTransformer();
        var receiver = SrtpTransformer();
        var packets = Enumerable.Range(10, 40).ToDictionary(seq => seq, seq => sender.Transform(RtpPacket((ushort)seq)));
        foreach (var seq in new[] { 10, 11, 13, 49, 12 })
            Assert.Equal(RtpPacket((ushort)seq), receiver.ReverseTransform(packets[seq]));
        foreach (var seq in new[] { 10, 11, 12, 13, 49 })
            Assert.Null(receiver.ReverseTransform(packets[seq]));
    }

    [Fact]
    public void Srtp_packets_around_a_rollover_decrypt_in_any_order()
    {
        var sender = SrtpTransformer();
        var receiver = SrtpTransformer();
        ushort[] sent = [65533, 65534, 65535, 0, 1, 2, 3];
        var packets = sent.ToDictionary(seq => seq, seq => sender.Transform(RtpPacket(seq)));
        foreach (var seq in new ushort[] { 65533, 0, 1, 65534, 65535, 2, 3 })
            Assert.Equal(RtpPacket(seq), receiver.ReverseTransform(packets[seq]));
    }

    [Fact]
    public async Task Oversized_datagram_does_not_abort_the_dtls_handshake()
    {
        var client = new DtlsSrtpTransport(new DtlsSrtpClient()) { TimeoutMilliseconds = 10000 };
        var server = new DtlsSrtpTransport(new DtlsSrtpServer()) { TimeoutMilliseconds = 10000 };
        client.OnDataReady += server.WriteToRecvStream;
        server.OnDataReady += client.WriteToRecvStream;
        var oversized = new byte[2900];
        oversized[0] = 22;
        server.WriteToRecvStream(oversized);
        client.WriteToRecvStream(oversized);
        try
        {
            var handshakes = new[]
            {
                Task.Run(() => server.DoHandshake(out _)),
                Task.Run(() => client.DoHandshake(out _)),
            };
            Assert.All(await Task.WhenAll(handshakes).WaitAsync(TimeSpan.FromSeconds(10)), Assert.True);
        }
        finally
        {
            client.Close();
            server.Close();
        }
    }

    private static IPacketTransformer SrtpTransformer()
    {
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var salt = Enumerable.Range(101, 14).Select(i => (byte)i).ToArray();
        var policy = new SrtpPolicy(SrtpPolicy.AESCM_ENCRYPTION, 16, SrtpPolicy.HMACSHA1_AUTHENTICATION, 20, 10, 14);
        return new SrtpTransformEngine(key, salt, policy, policy).GetRTPTransformer();
    }

    private static byte[] RtpPacket(ushort seq)
    {
        var packet = new RTPPacket(20);
        packet.Header.SequenceNumber = seq;
        packet.Header.SyncSource = 0x11223344;
        packet.Header.Timestamp = 1000u * seq;
        packet.Header.PayloadType = 96;
        for (var i = 0; i < packet.Payload.Length; i++) packet.Payload[i] = (byte)(seq + i);
        return packet.GetBytes();
    }

    private static Socket BoundSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }
}
