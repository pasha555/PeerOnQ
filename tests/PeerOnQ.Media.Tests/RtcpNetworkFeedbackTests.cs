using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public class RtcpNetworkFeedbackTests
{
    [Fact]
    public async Task A_viewer_does_not_wait_for_receiver_reports_for_video_it_does_not_send()
    {
        await using var viewer = WebRtcMediaSession.CreateViewer(SessionId.New());

        Assert.NotEqual("awaiting_network_feedback", viewer.GetStatistics().QualityChangeReason);
    }

    [Theory]
    [InlineData(0.20, 0)]
    [InlineData(0, 100)]
    public void Expired_loss_or_jitter_neither_repeats_degradation_nor_implies_recovery(
        double packetLossFraction,
        double jitterMs)
    {
        var time = new MonotonicTimeProvider();
        var feedback = new RtcpNetworkFeedback(time);
        var controller = new AdaptiveQualityController(timeProvider: time);
        feedback.SetConnected(true);
        feedback.Report(packetLossFraction, jitterMs);

        Evaluate(controller, feedback);
        Assert.Equal(1, controller.Current.Index);
        Assert.Equal(ConnectionPolicyAction.Degrade, controller.LastDecision.Action);

        time.Advance(RtcpNetworkFeedback.Lifetime);
        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover * 3; i++)
        {
            Evaluate(controller, feedback);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, controller.Current.Index);
        Assert.Equal(ConnectionHealth.Unknown, controller.LastDecision.Health);
        Assert.Equal(ConnectionPolicyAction.Hold, controller.LastDecision.Action);
        Assert.Equal("awaiting_network_feedback", controller.LastDecision.ReasonCode);

        feedback.Report(packetLossFraction, jitterMs);
        Evaluate(controller, feedback);
        Assert.Equal(2, controller.Current.Index);
        Assert.Equal(ConnectionPolicyAction.Degrade, controller.LastDecision.Action);
    }

    [Fact]
    public void Fresh_healthy_feedback_allows_measured_recovery_after_an_expired_bad_report()
    {
        var time = new MonotonicTimeProvider();
        var feedback = new RtcpNetworkFeedback(time);
        var controller = new AdaptiveQualityController(timeProvider: time);
        feedback.SetConnected(true);
        feedback.Report(0.20, 0);
        Evaluate(controller, feedback);
        time.Advance(RtcpNetworkFeedback.Lifetime);
        Evaluate(controller, feedback);

        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover; i++)
        {
            feedback.Report(0, 0);
            Evaluate(controller, feedback);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(0, controller.Current.Index);
        Assert.Equal(ConnectionPolicyAction.Recover, controller.LastDecision.Action);
    }

    [Fact]
    public void A_disconnected_or_restarted_path_cannot_reuse_the_previous_report()
    {
        var feedback = new RtcpNetworkFeedback(new MonotonicTimeProvider());
        feedback.SetConnected(true);
        feedback.Report(0.20, 100);
        Assert.NotNull(feedback.GetFresh());

        feedback.SetConnected(false);
        feedback.Report(0.20, 100);
        Assert.Null(feedback.GetFresh());
        feedback.SetConnected(true);
        Assert.Null(feedback.GetFresh());

        feedback.Report(0, 1);
        feedback.SetConnected(true);
        Assert.Equal(1, feedback.GetFresh()!.JitterMs);

        feedback.Reset();
        Assert.Null(feedback.GetFresh());
        feedback.Report(0, 2);
        Assert.Equal(2, feedback.GetFresh()!.JitterMs);
    }

    [Fact]
    public void Receiver_report_expiry_uses_elapsed_time_instead_of_the_wall_clock()
    {
        var time = new MonotonicTimeProvider();
        var feedback = new RtcpNetworkFeedback(time);
        feedback.SetConnected(true);
        feedback.Report(0.20, 100);

        time.WallClockOffset = TimeSpan.FromDays(100);
        Assert.NotNull(feedback.GetFresh());
        time.WallClockOffset = TimeSpan.FromDays(-100);
        time.Advance(RtcpNetworkFeedback.Lifetime);
        Assert.Null(feedback.GetFresh());
    }

    [Fact]
    public void Rising_rtt_does_not_increase_the_current_path_transit_baseline()
    {
        var time = new MonotonicTimeProvider();
        var feedback = new RtcpNetworkFeedback(time);
        feedback.SetConnected(true);
        feedback.ObserveRtt(100);
        time.Advance(TimeSpan.FromSeconds(1));
        feedback.ObserveRtt(300);
        Assert.Equal(100, feedback.GetBaselineRttMs());

        feedback.ObserveRtt(80);
        feedback.ObserveRtt(250);
        Assert.Equal(80, feedback.GetBaselineRttMs());
    }

    [Fact]
    public void In_flight_or_invalid_rtt_samples_preserve_but_do_not_renew_a_fresh_baseline()
    {
        var time = new MonotonicTimeProvider();
        var feedback = new RtcpNetworkFeedback(time);
        feedback.SetConnected(true);
        feedback.ObserveRtt(120);
        time.Advance(RtcpNetworkFeedback.Lifetime / 2);

        foreach (var invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            feedback.ObserveRtt(invalid);
            Assert.Equal(120, feedback.GetBaselineRttMs());
        }

        time.WallClockOffset = TimeSpan.FromDays(100);
        Assert.Equal(120, feedback.GetBaselineRttMs());
        time.Advance(RtcpNetworkFeedback.Lifetime / 2);
        Assert.Equal(0, feedback.GetBaselineRttMs());
    }

    [Fact]
    public void A_valid_rtt_sample_after_expiry_starts_a_new_baseline()
    {
        var time = new MonotonicTimeProvider();
        var feedback = new RtcpNetworkFeedback(time);
        feedback.SetConnected(true);
        feedback.ObserveRtt(40);
        time.Advance(RtcpNetworkFeedback.Lifetime);

        feedback.ObserveRtt(160);

        Assert.Equal(160, feedback.GetBaselineRttMs());
        time.Advance(RtcpNetworkFeedback.Lifetime - TimeSpan.FromTicks(1));
        Assert.Equal(160, feedback.GetBaselineRttMs());
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(0, feedback.GetBaselineRttMs());
    }

    [Fact]
    public void Disconnect_and_explicit_path_reset_clear_the_rtt_baseline()
    {
        var feedback = new RtcpNetworkFeedback(new MonotonicTimeProvider());
        feedback.ObserveRtt(50);
        Assert.Equal(0, feedback.GetBaselineRttMs());
        feedback.SetConnected(true);
        feedback.ObserveRtt(100);
        feedback.SetConnected(true);
        Assert.Equal(100, feedback.GetBaselineRttMs());

        feedback.SetConnected(false);
        feedback.ObserveRtt(50);
        feedback.SetConnected(true);
        Assert.Equal(0, feedback.GetBaselineRttMs());
        feedback.ObserveRtt(150);
        feedback.Reset();
        Assert.Equal(0, feedback.GetBaselineRttMs());
        feedback.ObserveRtt(200);
        Assert.Equal(200, feedback.GetBaselineRttMs());
    }

    private static void Evaluate(AdaptiveQualityController controller, RtcpNetworkFeedback feedback)
    {
        var report = feedback.GetFresh();
        controller.Evaluate(new QualitySample
        {
            NetworkFeedbackAvailable = report is not null,
            PacketLossFraction = report?.PacketLossFraction ?? 0,
            JitterMs = report?.JitterMs ?? 0,
            QueueDrops = 0,
            FramesEncoded = 60,
        });
    }

    private sealed class MonotonicTimeProvider : TimeProvider
    {
        private TimeSpan _elapsed;
        public TimeSpan WallClockOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed + WallClockOffset;
        public void Advance(TimeSpan duration) => _elapsed += duration;
    }
}
