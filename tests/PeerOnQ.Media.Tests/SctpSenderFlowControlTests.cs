using System.Collections.Concurrent;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Reflection;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class SctpSenderFlowControlTests
{
    [Theory]
    [InlineData(2400u, 12000u)]
    [InlineData(12000u, 2400u)]
    public void A_burst_respects_each_flow_window_per_new_chunk(uint receiverWindow, uint congestionWindow)
    {
        using var fixture = new SenderFixture(receiverWindow, congestionWindow);
        fixture.QueueFourChunks();
        fixture.StartAndWaitForIdle();
        Assert.Equal(2, fixture.NewSends);
        Assert.Equal(2400u, fixture.OutstandingBytes);
    }

    [Fact]
    public void Zero_credit_with_existing_flight_waits_then_resumes_after_a_valid_ack()
    {
        using var fixture = new SenderFixture(1200, 12000, seedOutstanding: true);
        fixture.QueueFourChunks();
        fixture.StartAndWaitForIdle();
        Assert.Equal(0, fixture.NewSends);
        fixture.AcknowledgeSeed();
        Assert.True(SpinWait.SpinUntil(() => fixture.NewSends > 0, TimeSpan.FromSeconds(2)));
        fixture.WaitForIdle();
        Assert.Equal(1, fixture.NewSends);
        Assert.Equal(1200u, fixture.OutstandingBytes);
    }

    [Fact]
    public void A_handshake_receive_window_update_applies_before_the_first_data()
    {
        using var fixture = new SenderFixture(12000, 12000);
        fixture.SetReceiverWindow(2400);
        fixture.QueueFourChunks();
        fixture.StartAndWaitForIdle();
        Assert.Equal(2, fixture.NewSends);
    }

    [Fact]
    public void Existing_data_can_still_retransmit_when_new_data_credit_is_zero()
    {
        using var fixture = new SenderFixture(1200, 12000, seedOutstanding: true, expireSeed: true);
        fixture.SetReceiverWindow(0);
        fixture.QueueFourChunks();
        fixture.StartAndWaitForIdle();
        Assert.Equal(0, fixture.NewSends);
        Assert.Equal(1, fixture.RetransmittedSends);
        Assert.Equal(1200u, fixture.Get<uint>("_congestionWindow"));
        Assert.Equal(1200u, fixture.OutstandingBytes);
    }

    [Fact]
    public void Non_multiple_cwnd_allows_only_one_packet_of_overshoot()
    {
        using var fixture = new SenderFixture(12000, 4380);
        fixture.QueueFourChunks();
        fixture.StartAndWaitForIdle();
        Assert.Equal(4, fixture.NewSends);
        Assert.InRange(fixture.OutstandingBytes, 4380u, 4380u + 1199u);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(600u)]
    public void Insufficient_window_probes_are_bounded_then_resume_when_the_window_reopens(uint window)
    {
        using var fixture = new SenderFixture(window, 12000, probeTiming: true);
        fixture.QueueFourChunks();
        fixture.StartAndWaitForIdle();
        Assert.Equal(0, fixture.NewSends);
        Assert.True(SpinWait.SpinUntil(() => fixture.NewSends == 1, TimeSpan.FromSeconds(2)),
            "An empty flight must issue one delayed receive-window probe.");
        fixture.WaitForIdle();
        fixture.Acknowledge(100, window);
        fixture.WakeAndWaitForIdle();
        for (var attempt = 0; attempt < 8; attempt++) fixture.WakeAndWaitForIdle();
        Assert.Equal(1, fixture.NewSends);
        Assert.True(fixture.Get<double>("_receiverProbeIntervalMilliseconds") >= 2000);
        Assert.Equal(12000u, fixture.Get<uint>("_congestionWindow"));
        fixture.Acknowledge(100, 4800);
        Assert.True(SpinWait.SpinUntil(() => fixture.NewSends == 4, TimeSpan.FromSeconds(2)));
        fixture.WaitForIdle();
        Assert.Equal(0L, fixture.Get<long>("_nextReceiverProbeTimestamp"));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(600u)]
    public void An_unacknowledged_window_probe_backs_off_without_collapsing_cwnd(uint window)
    {
        using var fixture = new SenderFixture(window, 12000, probeTiming: true);
        fixture.QueueFourChunks();
        fixture.StartAndWaitForIdle();
        Assert.True(SpinWait.SpinUntil(() => fixture.NewSends == 1, TimeSpan.FromSeconds(2)));
        fixture.WaitForIdle();
        // A positive but insufficient update must retain probe-specific behavior.
        fixture.Acknowledge(99, 600);
        fixture.WakeAndWaitForIdle();
        fixture.ExpireOutstandingProbe();
        fixture.WakeAndWaitForIdle();
        Assert.Equal(1, fixture.RetransmittedSends);
        Assert.Equal(1, fixture.NewSends);
        Assert.Equal(12000u, fixture.Get<uint>("_congestionWindow"));
        Assert.False(fixture.Get<bool>("_inRetransmitMode"));
        Assert.True(fixture.Get<double>("_receiverProbeIntervalMilliseconds") >= 4000);
        fixture.Acknowledge(100, 4800);
        Assert.True(SpinWait.SpinUntil(() => fixture.NewSends == 4, TimeSpan.FromSeconds(2)));
        fixture.WaitForIdle();
        Assert.Equal(0L, fixture.Get<long>("_nextReceiverProbeTimestamp"));
    }

    [Fact]
    public void A_small_chunk_reserves_one_existing_receive_sequence_slot_until_acked()
    {
        using var fixture = new SenderFixture(1200, 12000);
        fixture.QueueSmallMessages();
        fixture.StartAndWaitForIdle();
        Assert.Equal(1, fixture.NewSends);
        Assert.Equal(100u, fixture.OutstandingBytes);
        fixture.Acknowledge(100, 1200);
        Assert.True(SpinWait.SpinUntil(() => fixture.NewSends == 2, TimeSpan.FromSeconds(2)));
        fixture.WaitForIdle();
        Assert.Equal(2, fixture.NewSends);
        Assert.Equal(100u, fixture.OutstandingBytes);
    }

    [Fact]
    public void A_fitting_short_final_fragment_can_use_positive_credit_below_one_mtu()
    {
        using var fixture = new SenderFixture(600, 12000);
        fixture.QueueShortFinalFragment();
        fixture.StartAndWaitForIdle();
        Assert.Equal(1, fixture.NewSends);
        Assert.Equal(500u, fixture.OutstandingBytes);
        Assert.Equal(0L, fixture.Get<long>("_nextReceiverProbeTimestamp"));
    }

    private sealed class SenderFixture : IDisposable
    {
        private readonly SctpDataSender sender;
        private Thread? worker;
        private Exception? workerFailure;
        private int newSends;
        private int retransmittedSends;
        public int NewSends => Volatile.Read(ref newSends);
        public int RetransmittedSends => Volatile.Read(ref retransmittedSends);
        public uint OutstandingBytes => Get<uint>("_outstandingBytes", property: true);
        public SenderFixture(uint receiverWindow, uint congestionWindow, bool seedOutstanding = false, bool expireSeed = false, bool probeTiming = false)
        {
            sender = new SctpDataSender("normal-flow-control-test", Sent, 1200, 100, receiverWindow);
            Set("_congestionWindow", congestionWindow);
            Set("_burstPeriodMilliseconds", 120000);
            Set("_rtoMinimumMilliseconds", probeTiming ? 1000 : 120000);
            if (probeTiming)
            {
                Set("_burstPeriodMilliseconds", 20);
                Set("_rtoInitialMilliseconds", 1000);
            }
            if (seedOutstanding)
            {
                Set("_gotFirstSACK", true);
                Set("_cumulativeAckTSN", 99u);
                typeof(SctpDataSender).GetProperty("TSN")!.SetValue(sender, 101u);
                var chunk = new SctpDataChunk(false, true, true, 100, 0, 0, 53, new byte[1200]);
                WriteChunk(chunk, "SendCount", 1);
                WriteChunk(chunk, "LastSentAt", DateTime.Now.AddSeconds(expireSeed ? -5 : 0));
                Get<ConcurrentDictionary<uint, SctpDataChunk>>("_unconfirmedChunks").TryAdd(100, chunk);
                Set("_hasRoundTripTime", true);
                Set("_rto", 1000d);
            }
        }
        public void ExpireOutstandingProbe()
        {
            var pending = Get<ConcurrentDictionary<uint, SctpDataChunk>>("_unconfirmedChunks");
            WriteChunk(Assert.Single(pending.Values), "LastSentAt", DateTime.Now.AddSeconds(-5));
            Set("_nextReceiverProbeTimestamp", Stopwatch.GetTimestamp() - 1);
        }
        public void QueueSmallMessages()
        {
            for (var index = 0; index < 4; index++) sender.SendData(0, 53, new byte[100]);
        }
        public void QueueShortFinalFragment() => sender.SendData(0, 53, new byte[500]);
        public void QueueFourChunks() => sender.SendData(0, 53, new byte[4800]);
        public void SetReceiverWindow(uint window) => sender.SetReceiverWindow(window);
        public void AcknowledgeSeed() => Acknowledge(100, 1200);
        public void Acknowledge(uint tsn, uint window) => sender.GotSack(new SctpSackChunk(tsn, window));
        public void WakeAndWaitForIdle()
        {
            var wake = Get<ManualResetEventSlim>("_senderMre");
            wake.Set();
            Assert.True(SpinWait.SpinUntil(() => !wake.IsSet && worker is not null &&
                (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2)));
        }
        public void StartAndWaitForIdle()
        {
            worker = new Thread(() =>
            {
                try { typeof(SctpDataSender).GetMethod("DoSend", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender, [null]); }
                catch (Exception error) { workerFailure = error; }
            }) { IsBackground = true };
            worker.Start();
            WaitForIdle();
        }
        public void WaitForIdle() => Assert.True(SpinWait.SpinUntil(() =>
            worker is not null && (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(2)), "The owned sender did not reach its idle wait.");
        private void Sent(SctpDataChunk chunk)
        {
            var sendCount = (int)typeof(SctpDataChunk).GetField("SendCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunk)!;
            if (sendCount == 1) Interlocked.Increment(ref newSends);
            else Interlocked.Increment(ref retransmittedSends);
        }
        public T Get<T>(string name, bool property = false) => (T)(property
            ? typeof(SctpDataSender).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!
            : typeof(SctpDataSender).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!);
        private void Set(string name, object value) => typeof(SctpDataSender)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, value);
        private static void WriteChunk(SctpDataChunk chunk, string name, object value) => typeof(SctpDataChunk)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(chunk, value);
        public void Dispose()
        {
            sender.Close();
            Get<ManualResetEventSlim>("_senderMre").Set();
            if (worker is not null) Assert.True(worker.Join(TimeSpan.FromSeconds(2)), "Owned normal sender failed to stop.");
            Assert.Null(workerFailure);
        }
    }
}
