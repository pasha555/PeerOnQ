using System.Collections.Concurrent;
using Org.BouncyCastle.Crypto.Tls;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using Xunit;

namespace PeerOnQ.Media.Tests;

// Hostile-boundary regressions for the PeerOnQ.SIPSorcery 8.0.14 SCTP patches.
// Uses only public SIPSorcery APIs; every potentially endless operation runs under a timeout.
public sealed class SctpMalformedInputRegressionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(SctpChunkType.SACK)]
    [InlineData(SctpChunkType.INIT)]
    [InlineData(SctpChunkType.COOKIE_ACK)]
    [InlineData(SctpChunkType.HEARTBEAT)]
    public async Task A_zero_length_chunk_is_rejected_without_an_endless_walk(SctpChunkType type)
    {
        var packet = Packet(Chunk((byte)type, 0, 24));
        var parse = Task.Run(() => SctpPacket.Parse(packet, 0, packet.Length));
        Assert.Same(parse, await Task.WhenAny(parse, Task.Delay(Timeout)));
        await Assert.ThrowsAsync<ApplicationException>(() => parse);
    }

    [Theory]
    [InlineData(ushort.MaxValue, 0)]
    [InlineData(0, ushort.MaxValue)]
    [InlineData(ushort.MaxValue, ushort.MaxValue)]
    public void Sack_report_counts_beyond_the_chunk_are_rejected_inside_a_large_receive_buffer(ushort gaps, ushort duplicates)
    {
        var sack = Chunk((byte)SctpChunkType.SACK, 16, 16);
        NetConvert.ToBuffer(gaps, sack, 12);
        NetConvert.ToBuffer(duplicates, sack, 14);
        var packet = Packet(sack);
        var receiveBuffer = new byte[262144];
        packet.CopyTo(receiveBuffer, 0);

        Assert.IsType<ApplicationException>(Record.Exception(() => SctpSackChunk.ParseChunk(sack, 0)));
        Assert.IsType<ApplicationException>(Record.Exception(() => SctpPacket.Parse(receiveBuffer, 0, packet.Length)));
    }

    [Fact]
    public async Task A_gap_block_ending_at_the_maximum_offset_terminates()
    {
        var sent = 0;
        var flight = new ManualResetEventSlim();
        var sender = new SctpDataSender("hostile-gap-block", _ =>
        {
            if (Interlocked.Increment(ref sent) == 3) flight.Set();
        }, 1200, 1000, 262144);
        try
        {
            sender.SendData(0, 53, new byte[3600]);
            sender.StartSending();
            Assert.True(flight.Wait(Timeout));
            sender.GotSack(new SctpSackChunk(1000, 262144));

            var hostile = new SctpSackChunk(1000, 262144);
            for (var block = 0; block < 16379; block++)
                hostile.GapAckBlocks.Add(new SctpTsnGapBlock { Start = 1, End = ushort.MaxValue });
            var gotSack = Task.Run(() => sender.GotSack(hostile));
            Assert.Same(gotSack, await Task.WhenAny(gotSack, Task.Delay(Timeout)));
            await gotSack;
        }
        finally
        {
            sender.Close();
        }
    }

    [Fact]
    public void The_dtls_receive_loop_survives_malformed_and_failing_packets()
    {
        var dtls = new QueueDatagramTransport();
        var transport = new RTCSctpTransport(5000, 5000, 0);
        var closed = 0;
        transport.OnStateChanged += state =>
        {
            if (state == RTCSctpTransportState.Closed) Interlocked.Increment(ref closed);
        };
        transport.Start(dtls, false);
        try
        {
            dtls.Deliver(WithChecksum(Packet(Chunk((byte)SctpChunkType.COOKIE_ACK, 0, 8))));
            var sack = Chunk((byte)SctpChunkType.SACK, 16, 16);
            NetConvert.ToBuffer(ushort.MaxValue, sack, 12);
            dtls.Deliver(WithChecksum(Packet(sack)));
            dtls.Deliver(Init(chunks: 2)); // Well formed, but its processing throws a non-parse exception.
            dtls.Deliver(Init(chunks: 1));

            Assert.True(dtls.Sent.TryTake(out var response, Timeout), "The receive loop stopped answering.");
            var initAck = SctpPacket.Parse(response, 0, response.Length);
            Assert.Equal(SctpChunkType.INIT_ACK, Assert.Single(initAck.Chunks).KnownType);
            Assert.Equal(0, Volatile.Read(ref closed));
        }
        finally
        {
            transport.Close();
        }
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref closed) == 1, Timeout));
    }

    private static byte[] Packet(byte[] chunk)
    {
        var packet = new byte[SctpHeader.SCTP_HEADER_LENGTH + chunk.Length];
        new SctpHeader { SourcePort = 5000, DestinationPort = 5000, VerificationTag = 0x01020304 }.WriteToBuffer(packet, 0);
        chunk.CopyTo(packet, SctpHeader.SCTP_HEADER_LENGTH);
        return packet;
    }

    private static byte[] Chunk(byte type, int declaredLength, int totalBytes)
    {
        var chunk = new byte[totalBytes];
        chunk[0] = type;
        NetConvert.ToBuffer((ushort)declaredLength, chunk, 2);
        return chunk;
    }

    private static byte[] WithChecksum(byte[] packet)
    {
        NetConvert.ToBuffer(0U, packet, SctpPacket.CHECKSUM_BUFFER_POSITION);
        var checksum = CRC32C.Calculate(packet, 0, packet.Length);
        NetConvert.ToBuffer(NetConvert.EndianFlip(checksum), packet, SctpPacket.CHECKSUM_BUFFER_POSITION);
        return packet;
    }

    private static byte[] Init(int chunks)
    {
        var packet = new SctpPacket(5000, 5000, 0);
        for (var index = 0; index < chunks; index++)
            packet.AddChunk(new SctpInitChunk(SctpChunkType.INIT, 0x01020304, 1, 131072, 16, 16));
        return packet.GetBytes();
    }

    private sealed class QueueDatagramTransport : DatagramTransport
    {
        private readonly BlockingCollection<byte[]> received = new();
        public BlockingCollection<byte[]> Sent { get; } = new();
        public void Deliver(byte[] packet) => received.Add(packet);
        public int GetReceiveLimit() => 262144;
        public int GetSendLimit() => 262144;
        public int Receive(byte[] buf, int off, int len, int waitMillis)
        {
            if (!received.TryTake(out var packet, waitMillis)) return DtlsSrtpTransport.DTLS_RETRANSMISSION_CODE;
            packet.CopyTo(buf, off);
            return packet.Length;
        }
        public void Send(byte[] buf, int off, int len) => Sent.Add(buf.AsSpan(off, len).ToArray());
        public void Close() { }
    }
}
