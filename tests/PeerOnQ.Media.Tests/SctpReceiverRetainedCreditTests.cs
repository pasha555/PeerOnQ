using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class SctpReceiverRetainedCreditTests
{
    private const uint Window = 262144;
    private const int FragmentSize = 1200;

    [Theory]
    [InlineData(65536, 196800u)]
    [InlineData(245760, 16800u)]
    [InlineData(248832, 13200u)] // Full 240 KiB chunk with additional wire-envelope space.
    public void A_fragment_gap_reduces_credit_once_per_unique_payload_until_delivery(int payloadLength, uint expected)
    {
        const uint firstTsn = 100;
        var receiver = new SctpDataReceiver(Window, FragmentSize, firstTsn);
        var bytes = Enumerable.Range(0, payloadLength).Select(index => (byte)(index % 251)).ToArray();
        var chunks = Fragment(bytes, firstTsn, 0);

        Assert.Empty(receiver.OnDataChunk(chunks[0]));
        // Delay one ordinary fragment; all subsequent fragments stay inside the normal 218-TSN window.
        foreach (var chunk in chunks.Skip(2))
        {
            Assert.Empty(receiver.OnDataChunk(chunk));
            Assert.NotNull(receiver.GetSackChunk());
        }
        // Advertised bytes also fit the remaining existing sequence slots;
        // the division remainder cannot fund another short DATA chunk.
        Assert.Equal(expected, receiver.GetSackChunk().ARwnd);
        var beforeDuplicate = receiver.GetSackChunk().ARwnd;
        Assert.Empty(receiver.OnDataChunk(chunks[2]));
        Assert.Equal(beforeDuplicate, receiver.GetSackChunk().ARwnd);

        var delivered = Assert.Single(receiver.OnDataChunk(chunks[1]));
        Assert.Equal(bytes, delivered.UserData);
        var restored = receiver.GetSackChunk();
        Assert.Equal(firstTsn + (uint)chunks.Count - 1, restored.CumulativeTsnAck);
        Assert.Empty(restored.GapAckBlocks);
        Assert.Equal(Window, restored.ARwnd);
    }

    [Fact]
    public void A_completed_ordered_message_retains_credit_without_counting_its_fragments_twice()
    {
        const uint firstTsn = 100;
        var receiver = new SctpDataReceiver(Window, FragmentSize, firstTsn);
        Assert.Single(receiver.OnDataChunk(new SctpDataChunk(false, true, true, firstTsn, 0, 0, 53, [1])));
        var bytes = new byte[245760];
        var chunks = Fragment(bytes, firstTsn + 1, 2);
        foreach (var chunk in chunks)
        {
            Assert.Empty(receiver.OnDataChunk(chunk));
            Assert.NotNull(receiver.GetSackChunk());
        }
        Assert.Equal(Window - (uint)bytes.Length, receiver.GetSackChunk().ARwnd);
        Assert.Empty(receiver.OnDataChunk(chunks[^1]));
        Assert.Equal(Window - (uint)bytes.Length, receiver.GetSackChunk().ARwnd);

        var delivered = receiver.OnDataChunk(new SctpDataChunk(false, true, true,
            firstTsn + (uint)chunks.Count + 1, 0, 1, 53, [2]));
        Assert.Equal(2, delivered.Count);
        Assert.Equal(new byte[] { 2 }, delivered[0].UserData);
        Assert.Equal(bytes, delivered[1].UserData);
        Assert.Equal(Window, receiver.GetSackChunk().ARwnd);
    }

    [Fact]
    public void Already_delivered_other_stream_data_still_consumes_sequence_tracking_credit()
    {
        var receiver = new SctpDataReceiver(Window, FragmentSize, 100);
        Assert.Single(receiver.OnDataChunk(new SctpDataChunk(false, true, true, 100, 0, 0, 53, [1])));
        // These ordinary small unordered messages are delivered immediately,
        // while one earlier TSN remains delayed on another stream.
        for (uint tsn = 102; tsn <= 300; tsn++)
            Assert.Single(receiver.OnDataChunk(new SctpDataChunk(true, true, true, tsn, 1, 0, 53, [5])));
        Assert.Equal(21600u, receiver.GetSackChunk().ARwnd);
        Assert.Empty(receiver.OnDataChunk(new SctpDataChunk(true, true, true, 102, 1, 0, 53, [5])));
        Assert.Equal(21600u, receiver.GetSackChunk().ARwnd);
        Assert.Single(receiver.OnDataChunk(new SctpDataChunk(false, true, true, 101, 0, 1, 53, [2])));
        Assert.Equal(300u, receiver.GetSackChunk().CumulativeTsnAck);
        Assert.Equal(Window, receiver.GetSackChunk().ARwnd);
    }

    private static List<SctpDataChunk> Fragment(byte[] bytes, uint firstTsn, ushort streamSequence)
    {
        var chunks = new List<SctpDataChunk>();
        for (var offset = 0; offset < bytes.Length; offset += FragmentSize)
        {
            var length = Math.Min(FragmentSize, bytes.Length - offset);
            chunks.Add(new SctpDataChunk(false, offset == 0, offset + length == bytes.Length,
                firstTsn + (uint)chunks.Count, 0, streamSequence, 53, bytes.AsSpan(offset, length).ToArray()));
        }
        return chunks;
    }
}
