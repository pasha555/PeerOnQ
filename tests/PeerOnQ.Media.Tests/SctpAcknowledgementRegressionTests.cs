using System.Collections.Concurrent;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class SctpAcknowledgementRegressionTests
{
    [Fact]
    public async Task Ordinary_gap_acknowledgement_preserves_fragment_reassembly_and_cumulative_ack()
    {
        var receiver = new SctpDataReceiver(262144, 1300, 10);
        var payload = Enumerable.Range(0, 5200).Select(index => (byte)index).ToArray();
        var heldData = new TaskCompletionSource<SctpDataChunk>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialDataSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportedGaps = new ConcurrentQueue<(ushort Start, ushort End)>();
        SctpDataSender? sender = null;
        sender = new SctpDataSender("normal-gap-acknowledgement", chunk =>
        {
            if (chunk.TSN == 11)
            {
                heldData.TrySetResult(chunk);
                return;
            }

            receiver.OnDataChunk(chunk);
            var sack = receiver.GetSackChunk();
            foreach (var gap in sack.GapAckBlocks)
                reportedGaps.Enqueue((gap.Start, gap.End));
            sender!.GotSack(sack);
            if (chunk.TSN == 13)
                initialDataSent.TrySetResult();
        }, 1300, 10, 262144);

        try
        {
            sender.SendData(0, 53, payload);
            sender.StartSending();
            await initialDataSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(((ushort)2, (ushort)3), reportedGaps);

            var frames = receiver.OnDataChunk(await heldData.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            sender.GotSack(receiver.GetSackChunk());

            Assert.Equal(payload, Assert.Single(frames).UserData);
            Assert.Equal(13u, receiver.CumulativeAckTSN);
            Assert.Equal(0ul, sender.BufferedAmount);
            Assert.Empty(receiver.GetSackChunk().GapAckBlocks);
        }
        finally
        {
            sender.Close();
        }
    }
}
