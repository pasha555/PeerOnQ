using System.Collections.Concurrent;
using System.Reflection;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class SctpSenderAckGrowthTests
{
    [Fact]
    public void A_full_flight_ack_recovers_the_one_mtu_congestion_window()
    {
        var fixture = new SenderFixture(1200, 4800);
        fixture.AddFlight(100);
        fixture.Acknowledge(100);
        Assert.Equal(2400u, fixture.CongestionWindow);
    }

    [Fact]
    public void Repeated_duplicate_and_stale_acks_cannot_inflate_the_window()
    {
        var fixture = new SenderFixture(1200, 4800);
        fixture.AddFlight(100);
        fixture.Acknowledge(100);
        fixture.SetCongestionWindow(3000);
        fixture.AddFlight(101, 102, 103);
        for (var repeat = 0; repeat < 8; repeat++)
        {
            fixture.Acknowledge(100);
            fixture.Acknowledge(99);
        }
        Assert.Equal(3000u, fixture.CongestionWindow);
    }

    [Fact]
    public void An_underutilized_window_does_not_grow_on_new_acks()
    {
        var fixture = new SenderFixture(4800, 12000);
        fixture.AddFlight(100);
        fixture.Acknowledge(100);
        Assert.Equal(4800u, fixture.CongestionWindow);
    }

    [Fact]
    public void Congestion_avoidance_requires_a_window_of_newly_acknowledged_bytes()
    {
        var fixture = new SenderFixture(4800, 2400);
        fixture.AddFlight(100, 101, 102, 103);
        for (uint ack = 100; ack < 103; ack++)
        {
            fixture.Acknowledge(ack);
            Assert.Equal(4800u, fixture.CongestionWindow);
            fixture.AddFlight(ack + 4);
        }
        fixture.Acknowledge(103);
        Assert.Equal(6000u, fixture.CongestionWindow);
    }

    private sealed class SenderFixture
    {
        private readonly SctpDataSender sender = new("normal-ack-growth-test", _ => { }, 1200, 100, 262144);
        public uint CongestionWindow => Read<uint>("_congestionWindow");
        public SenderFixture(uint congestionWindow, uint slowStartThreshold)
        {
            Set("_gotFirstSACK", true);
            Set("_cumulativeAckTSN", 99u);
            Set("_congestionWindow", congestionWindow);
            Set("_slowStartThreshold", slowStartThreshold);
        }

        public void SetCongestionWindow(uint window) => Set("_congestionWindow", window);
        public void AddFlight(params uint[] sequenceNumbers)
        {
            var pending = Read<ConcurrentDictionary<uint, SctpDataChunk>>("_unconfirmedChunks");
            foreach (var tsn in sequenceNumbers)
            {
                var chunk = new SctpDataChunk(false, true, true, tsn, 0, 0, 53, new byte[1200]);
                WriteChunk(chunk, "SendCount", 1);
                WriteChunk(chunk, "LastSentAt", DateTime.Now);
                Assert.True(pending.TryAdd(tsn, chunk));
            }
            typeof(SctpDataSender).GetProperty("TSN")!.SetValue(sender, sequenceNumbers.Max() + 1);
        }

        public void Acknowledge(uint tsn) => sender.GotSack(new SctpSackChunk(tsn, 262144));
        private T Read<T>(string name) => (T)typeof(SctpDataSender)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
        private void Set(string name, object value) => typeof(SctpDataSender)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, value);
        private static void WriteChunk(SctpDataChunk chunk, string name, object value) => typeof(SctpDataChunk)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(chunk, value);
    }
}
