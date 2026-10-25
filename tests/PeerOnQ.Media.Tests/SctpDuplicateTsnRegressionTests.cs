using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class SctpDuplicateTsnRegressionTests
{
    [Fact]
    public void Sack_reports_only_duplicates_received_since_previous_report()
    {
        var receiver = new SctpDataReceiver(262144, 1300, 1);
        receiver.OnDataChunk(Chunk(1));
        receiver.OnDataChunk(Chunk(3));
        receiver.OnDataChunk(Chunk(3));

        var first = receiver.GetSackChunk();
        Assert.Equal(new uint[] { 3 }, first.DuplicateTSN);
        Assert.Empty(receiver.GetSackChunk().DuplicateTSN);
        Assert.Equal(new uint[] { 3 }, first.DuplicateTSN);

        receiver.OnDataChunk(Chunk(3));
        Assert.Equal(new uint[] { 3 }, receiver.GetSackChunk().DuplicateTSN);
        Assert.Empty(receiver.GetSackChunk().DuplicateTSN);
    }

    [Fact]
    public void Repeated_gap_recovery_keeps_acknowledgements_bounded_and_advances_cumulative_tsn()
    {
        const int mtu = 1300;
        var receiver = new SctpDataReceiver(262144, mtu, 1);
        receiver.OnDataChunk(Chunk(1));
        uint cumulative = 1;

        for (var cycle = 0; cycle < 10000; cycle++)
        {
            var future = cumulative + 2;
            receiver.OnDataChunk(Chunk(future));
            receiver.OnDataChunk(Chunk(future));
            var sack = receiver.GetSackChunk();

            Assert.Equal(new uint[] { future }, sack.DuplicateTSN);
            Assert.Equal(cumulative, sack.CumulativeTsnAck);
            Assert.InRange(12 + sack.GetChunkLength(true), 0, mtu);
            var bytes = new byte[sack.GetChunkLength(true)];
            Assert.Equal(bytes.Length, sack.WriteTo(bytes, 0));

            receiver.OnDataChunk(Chunk(cumulative + 1));
            cumulative = future;
            var recovered = receiver.GetSackChunk();
            Assert.Equal(cumulative, recovered.CumulativeTsnAck);
            Assert.Empty(recovered.GapAckBlocks);
            Assert.Empty(recovered.DuplicateTSN);
        }

        Assert.Equal(20001u, receiver.CumulativeAckTSN);
    }

    private static SctpDataChunk Chunk(uint tsn) => new(true, true, true, tsn, 0, 0, 53, [1]);
}
