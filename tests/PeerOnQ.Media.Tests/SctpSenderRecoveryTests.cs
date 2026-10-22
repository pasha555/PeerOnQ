using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class SctpSenderRecoveryTests
{
    [Fact]
    public void Fast_loss_uses_ssthresh_and_does_not_reduce_the_same_recovery_window_again()
    {
        using var fixture = new SenderFixture();
        fixture.ReportGap(2);
        fixture.ReportGap(3);
        fixture.ReportGap(4);

        Assert.Equal(12000u, fixture.Get<uint>("_slowStartThreshold"));
        Assert.Equal(12000u, fixture.Get<uint>("_congestionWindow"));
        fixture.ReportGap(5);
        Assert.Equal(12000u, fixture.Get<uint>("_congestionWindow"));
    }

    [Fact]
    public void Further_normal_gap_reports_do_not_fast_retransmit_the_same_chunk_again()
    {
        using var fixture = new SenderFixture();
        fixture.ReportGap(2);
        fixture.ReportGap(3);
        fixture.ReportGap(4);
        fixture.Start();
        Assert.True(fixture.FirstMissingSend.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => fixture.Missing.TryGetValue(101, out var count) && count == 0,
            TimeSpan.FromSeconds(1)));

        fixture.Set("_congestionWindow", 48000u);
        fixture.ReportGap(5);
        fixture.ReportGap(6);
        fixture.ReportGap(7);
        fixture.QueueSentinel();

        // The sentinel proves the sender traversed the fast-retransmit branch; no negative sleep assertion.
        Assert.Equal(1, WaitHandle.WaitAny(
            [fixture.RepeatedMissingSend.WaitHandle, fixture.SentinelSent.WaitHandle], TimeSpan.FromSeconds(2)));
        Assert.Equal(1, fixture.MissingSends);
    }

    [Fact]
    public void An_earlier_T3_retransmission_does_not_forbid_the_first_fast_retransmission()
    {
        using var fixture = new SenderFixture(missingSendCount: 2);
        fixture.ReportGap(2);
        fixture.ReportGap(3);
        fixture.ReportGap(4);
        fixture.Set("_congestionWindow", 48000u);
        fixture.QueueSentinel();
        fixture.Start();

        Assert.Equal(0, WaitHandle.WaitAny(
            [fixture.FirstMissingSend.WaitHandle, fixture.SentinelSent.WaitHandle], TimeSpan.FromSeconds(2)));
        Assert.Equal(3, SenderFixture.ReadChunk<int>(fixture.Pending[101], "SendCount"));
    }

    [Fact]
    public void T3_retransmission_remains_available_after_a_fast_retransmission()
    {
        using var fixture = new SenderFixture();
        fixture.ReportGap(2);
        fixture.ReportGap(3);
        fixture.ReportGap(4);
        fixture.Start();
        Assert.True(fixture.FirstMissingSend.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => fixture.Missing.TryGetValue(101, out var count) && count == 0,
            TimeSpan.FromSeconds(1)));

        SenderFixture.WriteChunk(fixture.Pending[101], "LastSentAt", DateTime.Now.AddSeconds(-200));
        fixture.Wake();
        Assert.True(fixture.RepeatedMissingSend.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => fixture.Get<bool>("_inRetransmitMode"), TimeSpan.FromSeconds(1)));
        Assert.Equal(3, SenderFixture.ReadChunk<int>(fixture.Pending[101], "SendCount"));
        Assert.Equal(1200u, fixture.Get<uint>("_congestionWindow"));
    }

    [Fact]
    public void Fast_recovery_exit_waits_for_the_highest_sent_outstanding_chunk()
    {
        using var fixture = new SenderFixture();
        fixture.QueueSentinel(); // Queued TSNs must not extend the current sent flight.
        fixture.ReportGap(2);
        fixture.ReportGap(3);
        fixture.ReportGap(4);
        Assert.Equal(111u, fixture.Get<uint>("_fastRecoveryExitPoint"));
        fixture.ReportCumulative(104);
        Assert.True(fixture.Get<bool>("_inFastRecoveryMode"));
        fixture.ReportCumulative(111);
        Assert.False(fixture.Get<bool>("_inFastRecoveryMode"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retransmission_starts_with_the_lowest_outstanding_tsn(bool fast)
    {
        using var fixture = new SenderFixture();
        fixture.ConfigureOrderingCase(fast);
        fixture.Start();
        Assert.True(fixture.FirstPacketSent.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(101L, fixture.FirstPacketTsn);
    }

    [Fact]
    public void Concurrent_normal_missing_reports_do_not_fault_the_owned_sender()
    {
        using var fixture = new SenderFixture();
        fixture.ConfigureConcurrentReports();
        fixture.Start();
        Assert.True(fixture.FirstPacketSent.Wait(TimeSpan.FromSeconds(2)));
        // Model the existing SACK receive thread adding/removing ordinary missing
        // entries while the owned sender orders the same valid in-flight TSNs.
        for (var report = 0; report < 20000; report++)
        {
            uint tsn = 102u + (uint)(report % 99);
            fixture.Missing[tsn] = 3;
            if ((report & 1) == 0) fixture.Missing.TryRemove(tsn, out _);
            fixture.Wake();
            if ((report & 127) == 0) Thread.Yield();
        }
        // Dispose joins the actual sender and asserts any worker exception.
    }

    [Fact]
    public void An_earliest_expired_T3_is_not_starved_by_a_higher_fresh_fast_retransmission()
    {
        using var fixture = new SenderFixture();
        fixture.ConfigureExpiredPriority();
        fixture.Start();
        Assert.True(fixture.FirstPacketSent.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(101L, fixture.FirstPacketTsn);
    }

    private sealed class SenderFixture : IDisposable
    {
        private readonly SctpDataSender sender;
        private Thread? worker;
        private Exception? workerFailure;
        private int missingSends;
        private long firstPacketTsn = -1;
        private bool stopAfterFirst;
        public ManualResetEventSlim FirstPacketSent { get; } = new();
        public long FirstPacketTsn => Interlocked.Read(ref firstPacketTsn);
        public ManualResetEventSlim FirstMissingSend { get; } = new();
        public ManualResetEventSlim RepeatedMissingSend { get; } = new();
        public ManualResetEventSlim SentinelSent { get; } = new();
        public int MissingSends => Volatile.Read(ref missingSends);
        public ConcurrentDictionary<uint, SctpDataChunk> Pending =>
            Get<ConcurrentDictionary<uint, SctpDataChunk>>("_unconfirmedChunks");
        public ConcurrentDictionary<uint, int> Missing => Get<ConcurrentDictionary<uint, int>>("_missingChunks");

        public SenderFixture(int missingSendCount = 1)
        {
            sender = new SctpDataSender("normal-recovery-test", Sent, 1200, 100, 262144);
            Set("_gotFirstSACK", true);
            Set("_cumulativeAckTSN", 100u);
            Set("_congestionWindow", 24000u);
            Set("_hasRoundTripTime", true);
            Set("_rto", 120000d);
            Set("_rtoMaximumMilliseconds", 120000);
            typeof(SctpDataSender).GetProperty("TSN")!.SetValue(sender, 112u);
            for (uint tsn = 101; tsn <= 111; tsn++)
            {
                var chunk = new SctpDataChunk(false, true, true, tsn, 0, (ushort)(tsn - 100), 53, new byte[1200]);
                WriteChunk(chunk, "SendCount", tsn == 101 ? missingSendCount : 1);
                WriteChunk(chunk, "LastSentAt", DateTime.Now);
                Assert.True(Pending.TryAdd(tsn, chunk));
            }
        }

        public void ReportGap(ushort end)
        {
            var sack = new SctpSackChunk(100, 262144);
            sack.GapAckBlocks.Add(new SctpTsnGapBlock { Start = 2, End = end });
            sender.GotSack(sack);
        }

        public void ReportCumulative(uint tsn) => sender.GotSack(new SctpSackChunk(tsn, 262144));
        public void ConfigureOrderingCase(bool fast)
        {
            Pending.Clear();
            Missing.Clear();
            for (uint tsn = 101; tsn <= 200; tsn++)
            {
                var chunk = new SctpDataChunk(false, true, true, tsn, 0, (ushort)(tsn - 100), 53, new byte[1200]);
                WriteChunk(chunk, "SendCount", 1);
                WriteChunk(chunk, "LastSentAt", DateTime.Now.AddSeconds(fast ? 0 : -200));
                Assert.True(Pending.TryAdd(tsn, chunk));
                if (fast) Assert.True(Missing.TryAdd(tsn, 3));
            }
            typeof(SctpDataSender).GetProperty("TSN")!.SetValue(sender, 201u);
            Set("_congestionWindow", 120000u);
            Set("_inFastRecoveryMode", fast);
            Set("_inRetransmitMode", !fast);
            stopAfterFirst = true;
        }
        public void ConfigureExpiredPriority()
        {
            Missing.Clear();
            Assert.True(Missing.TryAdd(101, 3));
            Assert.True(Missing.TryAdd(105, 3));
            WriteChunk(Pending[101], "SendCount", 2);
            WriteChunk(Pending[101], "LastSentAt", DateTime.Now.AddSeconds(-200));
            Get<ConditionalWeakTable<SctpDataChunk, object>>("_fastRetransmittedChunks")
                .Add(Pending[101], new object());
            Set("_inFastRecoveryMode", true);
            stopAfterFirst = true;
        }
        public void ConfigureConcurrentReports()
        {
            ConfigureOrderingCase(fast: true);
            stopAfterFirst = false;
            Missing.Clear();
            Assert.True(Missing.TryAdd(101, 3));
            Set("_burstPeriodMilliseconds", 0);
            Set("_rtoMinimumMilliseconds", 0);
        }
        public void QueueSentinel() => sender.SendData(0, 53, [1]);
        public void Wake() => Get<ManualResetEventSlim>("_senderMre").Set();

        public void Start()
        {
            worker = new Thread(() =>
            {
                try { typeof(SctpDataSender).GetMethod("DoSend", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(sender, [null]); }
                catch (Exception error) { workerFailure = error; }
            }) { IsBackground = true };
            worker.Start();
        }

        private void Sent(SctpDataChunk chunk)
        {
            Interlocked.CompareExchange(ref firstPacketTsn, chunk.TSN, -1);
            if (stopAfterFirst) { sender.Close(); Wake(); }
            FirstPacketSent.Set();
            if (chunk.TSN == 101)
            {
                if (Interlocked.Increment(ref missingSends) == 1) FirstMissingSend.Set();
                else RepeatedMissingSend.Set();
            }
            if (chunk.TSN == 112) SentinelSent.Set();
        }

        public T Get<T>(string name) => (T)typeof(SctpDataSender)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
        public void Set(string name, object value) => typeof(SctpDataSender)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, value);
        public static T ReadChunk<T>(SctpDataChunk chunk, string name) =>
            (T)typeof(SctpDataChunk).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunk)!;
        public static void WriteChunk(SctpDataChunk chunk, string name, object value) =>
            typeof(SctpDataChunk).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(chunk, value);

        public void Dispose()
        {
            sender.Close();
            Wake();
            if (worker is not null) Assert.True(worker.Join(TimeSpan.FromSeconds(2)), "Normal sender worker failed to stop.");
            FirstPacketSent.Dispose();
            FirstMissingSend.Dispose();
            RepeatedMissingSend.Dispose();
            SentinelSent.Dispose();
            Assert.Null(workerFailure);
        }
    }
}
