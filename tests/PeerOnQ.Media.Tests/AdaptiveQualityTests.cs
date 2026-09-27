using PeerOnQ.Media.Pipeline;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Media.Tests;

public class AdaptiveQualityControllerTests
{
    private static QualitySample Healthy => new()
    {
        PacketLossFraction = 0.0,
        QueueDrops = 0,
        FramesEncoded = 60,
    };

    private static QualitySample Lossy(double loss) => new()
    {
        PacketLossFraction = loss,
        QueueDrops = 0,
        FramesEncoded = 60,
    };

    private static QualitySample Backpressured(long drops, long encoded) => new()
    {
        PacketLossFraction = 0,
        QueueDrops = drops,
        FramesEncoded = encoded,
    };

    [Fact]
    public async Task Manual_performance_profile_keeps_its_maximum_but_enables_latency_qos()
    {
        await using var session = WebRtcMediaSession.CreateSharer(
            SessionId.New(),
            capture: null,
            MediaProfile.For(QualityProfile.Performance));

        Assert.NotNull(session.Adaptive);
        Assert.Equal(60, session.Adaptive!.Current.TargetFps);
        Assert.Equal(12_000, session.Adaptive.Current.MaxBitrateKbps);
    }

    [Fact]
    public void It_starts_at_the_requested_quality()
    {
        var controller = new AdaptiveQualityController(maxFps: 30);

        Assert.Equal(0, controller.Current.Index);
        Assert.Equal(30, controller.Current.TargetFps);
        Assert.Equal(1, controller.Current.DownscaleFactor);
    }

    [Fact]
    public void The_ladder_drops_frame_rate_before_resolution()
    {
        var ladder = new AdaptiveQualityController(30).Ladder;

        Assert.True(ladder[1].TargetFps < ladder[0].TargetFps);
        Assert.Equal(1, ladder[1].DownscaleFactor);
        Assert.Equal(1, ladder[2].DownscaleFactor);

        // Resolution only degrades once frame rate alone was not enough.
        Assert.True(ladder[3].DownscaleFactor > 1);
        Assert.True(ladder[4].DownscaleFactor > ladder[3].DownscaleFactor);
    }

    [Fact]
    public async Task High_quality_ladder_preserves_the_requested_resolution()
    {
        var profile = MediaProfile.For(QualityProfile.Quality);
        await using var session = WebRtcMediaSession.CreateSharer(SessionId.New(), capture: null, profile);
        var controller = Assert.IsType<AdaptiveQualityController>(session.Adaptive);

        Assert.Equal(CaptureResolution.P2160, profile.Resolution);
        Assert.Equal(36_000, controller.Current.MaxBitrateKbps);
        Assert.All(controller.Ladder, level => Assert.Equal(1, level.DownscaleFactor));
    }

    [Fact]
    public void Missing_network_feedback_holds_quality_and_breaks_the_recovery_streak()
    {
        var controller = new AdaptiveQualityController();
        controller.Evaluate(Lossy(0.10));
        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover - 1; i++)
            controller.Evaluate(Healthy);

        for (var i = 0; i < 12; i++)
            controller.Evaluate(Healthy with { NetworkFeedbackAvailable = false });

        Assert.Equal(1, controller.Current.Index);
        Assert.Equal(ConnectionHealth.Unknown, controller.LastDecision.Health);
        Assert.Equal("awaiting_network_feedback", controller.LastDecision.ReasonCode);
        controller.Evaluate(Healthy);
        Assert.Equal(1, controller.Current.Index);
        for (var i = 1; i < AdaptiveQualityController.HealthyEvaluationsToRecover; i++)
            controller.Evaluate(Healthy);
        Assert.Equal(0, controller.Current.Index);
    }

    [Fact]
    public void Missing_network_feedback_still_allows_live_encoder_pressure_to_reduce_production()
    {
        var controller = new AdaptiveQualityController();

        controller.Evaluate(Backpressured(20, 60) with { NetworkFeedbackAvailable = false });

        Assert.Equal(1, controller.Current.Index);
        Assert.Equal("encoder_backpressure", controller.LastDecision.ReasonCode);
    }

    [Fact]
    public void Healthy_wan_transit_does_not_repeatedly_reduce_image_resolution()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(timeProvider: time);
        var sample = Healthy with
        {
            RttMs = 120,
            BaselineRttMs = 120,
            ReceiverInputToInjectionLatencyP95Ms = 80,
            ReceiverInputClockUncertaintyMs = 2,
            ReceiverCaptureToPresentLatencyP95Ms = 120,
            ReceiverFrameAgeClockUncertaintyMs = 2,
        };

        for (var i = 0; i < 20; i++)
        {
            controller.Evaluate(sample);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(0, controller.Current.Index);
        Assert.Equal(1, controller.Current.DownscaleFactor);
        Assert.Equal(ConnectionHealth.Good, controller.LastDecision.Health);
    }

    [Theory]
    [InlineData(100, 120, "viewer_input_latency")]
    [InlineData(80, 150, "viewer_frame_age")]
    public void Delay_beyond_estimated_wan_transit_still_reduces_production(
        double inputLatencyMs, double frameAgeMs, string expectedReason)
    {
        var controller = new AdaptiveQualityController();

        controller.Evaluate(Healthy with
        {
            RttMs = 120,
            BaselineRttMs = 120,
            ReceiverInputToInjectionLatencyP95Ms = inputLatencyMs,
            ReceiverInputClockUncertaintyMs = 2,
            ReceiverCaptureToPresentLatencyP95Ms = frameAgeMs,
            ReceiverFrameAgeClockUncertaintyMs = 2,
        });

        Assert.Equal(1, controller.Current.Index);
        Assert.Equal(expectedReason, controller.LastDecision.ReasonCode);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    public void Higher_rtt_does_not_hide_congestion_behind_a_larger_transit_allowance(double baselineRttMs)
    {
        var controller = new AdaptiveQualityController();
        controller.Evaluate(Healthy with
        {
            RttMs = 300,
            BaselineRttMs = baselineRttMs,
            ReceiverInputToInjectionLatencyP95Ms = 180,
            ReceiverInputClockUncertaintyMs = 2,
            ReceiverCaptureToPresentLatencyP95Ms = 200,
            ReceiverFrameAgeClockUncertaintyMs = 2,
        });

        Assert.Equal(1, controller.Current.Index);
        Assert.Equal("viewer_input_latency", controller.LastDecision.ReasonCode);
    }

    [Fact]
    public async Task Missing_current_feedback_caps_bulk_before_the_next_adaptive_tick()
    {
        await using var sharer = WebRtcMediaSession.CreateSharer(
            SessionId.New(), capture: null, MediaProfile.For(QualityProfile.Quality));
        sharer.Adaptive!.Evaluate(Healthy);
        Assert.Equal(ConnectionHealth.Excellent, sharer.Adaptive.LastDecision.Health);

        var statistics = sharer.GetStatistics();
        var allocation = ConnectionPolicyProvider.GetTransferAllocation(TransferPriorityMode.Balanced, statistics);

        Assert.Equal("awaiting_network_feedback", statistics.QualityChangeReason);
        Assert.Equal(ConnectionHealth.Unknown, statistics.ConnectionHealth);
        Assert.Equal(128, allocation.MaximumBulkKbps);
        Assert.Equal(64UL * 1024, allocation.MaximumBulkBufferedBytes);
    }

    [Fact]
    public void Static_screen_output_is_not_mistaken_for_insufficient_bandwidth()
    {
        var estimate = WebRtcMediaSession.EstimateAvailableOutgoingBitrateKbps(
            measuredBitrateKbps: 1_200,
            lossFraction: 0,
            rttMs: 12,
            jitterMs: 1,
            targetBitrateKbps: 36_000);

        Assert.Equal(0, estimate);
    }

    [Fact]
    public void Active_video_exposes_only_bounded_inferred_headroom_for_bulk_traffic()
    {
        var estimate = WebRtcMediaSession.EstimateAvailableOutgoingBitrateKbps(
            measuredBitrateKbps: 36_000,
            lossFraction: 0,
            rttMs: 12,
            jitterMs: 1,
            targetBitrateKbps: 36_000);

        Assert.InRange(estimate, 36_000.01, 43_200);
    }

    [Fact]
    public void Packet_loss_steps_the_quality_down()
    {
        var controller = new AdaptiveQualityController(30);

        var level = controller.Evaluate(Lossy(0.10));

        Assert.Equal(1, level.Index);
        Assert.True(level.TargetFps < 30);
    }

    [Fact]
    public void Sustained_loss_walks_down_the_whole_ladder_and_stops_at_the_bottom()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(30, timeProvider: time);
        var changes = new List<QualityLevel>();
        controller.LevelChanged += (_, level) => changes.Add(level);

        for (var i = 0; i < 4; i++)
            controller.Evaluate(Lossy(0.20));

        Assert.Equal(3, controller.Current.Index);
        time.Advance(AdaptiveQualityController.MinimumResolutionDwell);

        for (var i = 0; i < 6; i++)
            controller.Evaluate(Lossy(0.20));

        Assert.Equal(controller.Ladder.Count - 1, controller.Current.Index);
        Assert.Equal(4, controller.Current.DownscaleFactor);

        // It stops changing once the bottom rung is reached instead of thrashing.
        Assert.Equal(controller.Ladder.Count - 1, changes.Count);
    }

    [Fact]
    public void Resolution_cannot_oscillate_faster_than_the_dwell_window()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(30, timeProvider: time);

        controller.Evaluate(Lossy(0.20));
        controller.Evaluate(Lossy(0.20));
        controller.Evaluate(Lossy(0.20));
        Assert.Equal(2, controller.Current.DownscaleFactor);

        controller.Evaluate(Lossy(0.20));
        Assert.Equal(2, controller.Current.DownscaleFactor);

        time.Advance(AdaptiveQualityController.MinimumResolutionDwell);
        controller.Evaluate(Lossy(0.20));
        Assert.Equal(4, controller.Current.DownscaleFactor);

        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover; i++)
            controller.Evaluate(Healthy);
        Assert.Equal(4, controller.Current.DownscaleFactor);

        time.Advance(AdaptiveQualityController.MinimumResolutionDwell);
        controller.Evaluate(Healthy);
        Assert.Equal(2, controller.Current.DownscaleFactor);
    }

    [Fact]
    public void Local_backpressure_also_degrades_quality()
    {
        var controller = new AdaptiveQualityController(30);

        // A quarter of the frames were dropped by the queue: the encoder cannot keep up.
        var level = controller.Evaluate(Backpressured(drops: 20, encoded: 60));

        Assert.Equal(1, level.Index);
    }

    [Fact]
    public void Sustained_fresh_receiver_video_stall_degrades_after_two_windows()
    {
        var controller = new AdaptiveQualityController(30);
        var stalled = Healthy with
        {
            ReceiverFeedbackAvailable = true,
            ReceiverDecodeFps = 0,
            ReceiverRenderFps = 0,
        };

        var first = controller.Evaluate(stalled);
        var second = controller.Evaluate(stalled);

        Assert.Equal(0, first.Index);
        Assert.Equal(1, second.Index);
        Assert.Equal("viewer_video_stall", controller.LastDecision.ReasonCode);
        Assert.True(controller.IsReceiverPresentationStalled);

        controller.Reset();

        Assert.False(controller.IsReceiverPresentationStalled);
    }

    [Fact]
    public void High_measured_rtt_or_jitter_degrades_automatic_quality()
    {
        var controller = new AdaptiveQualityController(30);

        var highRtt = controller.Evaluate(new QualitySample
        {
            PacketLossFraction = 0,
            QueueDrops = 0,
            FramesEncoded = 60,
            RttMs = AdaptiveQualityController.DegradeRttMs + 1,
        });
        Assert.Equal(1, highRtt.Index);

        var highJitter = controller.Evaluate(new QualitySample
        {
            PacketLossFraction = 0,
            QueueDrops = 0,
            FramesEncoded = 60,
            JitterMs = AdaptiveQualityController.DegradeJitterMs + 1,
        });
        Assert.Equal(2, highJitter.Index);
    }

    [Fact]
    public void Measured_bandwidth_below_the_active_target_degrades_with_an_explainable_reason()
    {
        var controller = new AdaptiveQualityController(maxFps: 30, maxBitrateKbps: 4000);

        var level = controller.Evaluate(new QualitySample
        {
            PacketLossFraction = 0,
            QueueDrops = 0,
            FramesEncoded = 60,
            AvailableOutgoingBitrateKbps = 2500,
        });

        Assert.Equal(1, level.Index);
        Assert.Equal(ConnectionHealth.Poor, controller.LastDecision.Health);
        Assert.Equal(ConnectionPolicyAction.Degrade, controller.LastDecision.Action);
        Assert.Equal("insufficient_bandwidth", controller.LastDecision.ReasonCode);
    }

    [Fact]
    public void Reliable_viewer_frame_age_steps_quality_down()
    {
        var controller = new AdaptiveQualityController(maxFps: 30);

        var level = controller.Evaluate(new QualitySample
        {
            PacketLossFraction = 0,
            QueueDrops = 0,
            FramesEncoded = 60,
            ReceiverCaptureToPresentLatencyP95Ms = 120,
            ReceiverFrameAgeClockUncertaintyMs = 2,
            ReceiverCaptureToPresentTargetMs = 75,
        });

        Assert.Equal(1, level.Index);
        Assert.Equal("viewer_frame_age", controller.LastDecision.ReasonCode);
    }

    [Fact]
    public void Reliable_viewer_input_latency_steps_quality_down_after_bulk_has_priority()
    {
        var controller = new AdaptiveQualityController(maxFps: 30);

        var level = controller.Evaluate(new QualitySample
        {
            PacketLossFraction = 0,
            QueueDrops = 0,
            FramesEncoded = 60,
            ReceiverInputToInjectionLatencyP95Ms = 60,
            ReceiverInputClockUncertaintyMs = 2,
        });

        Assert.Equal(1, level.Index);
        Assert.Equal("viewer_input_latency", controller.LastDecision.ReasonCode);
    }

    [Fact]
    public void A_small_number_of_drops_is_tolerated()
    {
        var controller = new AdaptiveQualityController(30);

        controller.Evaluate(Backpressured(drops: 2, encoded: 60));

        Assert.Equal(0, controller.Current.Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Presentation_pressure_expires_only_when_slow_frames_stop_arriving(bool sustainedDelay)
    {
        var time = new ManualTimeProvider();
        var viewer = new MediaStatisticsCollector(timeProvider: time);
        var controller = new AdaptiveQualityController(timeProvider: time);
        viewer.FrameRendered(80, captureToPresentLatencyMs: 250, clockUncertaintyMs: 2);

        for (var second = 0; second < 60; second++)
        {
            // Fresh network feedback alone must not refresh an old latency sample. Actual
            // slow frames still need to trigger the existing congestion protection.
            if (sustainedDelay)
                viewer.FrameRendered(80, captureToPresentLatencyMs: 250, clockUncertaintyMs: 2);
            var feedback = viewer.Snapshot();
            controller.Evaluate(new QualitySample
            {
                NetworkFeedbackAvailable = true,
                PacketLossFraction = 0,
                RttMs = 10,
                QueueDrops = 0,
                FramesEncoded = 0,
                ReceiverFeedbackAvailable = true,
                ReceiverDecodeToRenderLatencyP95Ms = feedback.DecodeToRenderLatencyP95Ms,
                ReceiverCaptureToPresentLatencyP95Ms = feedback.CaptureToPresentLatencyP95Ms,
                ReceiverFrameAgeClockUncertaintyMs = feedback.FrameAgeClockUncertaintyMs,
            });
            if (second == 0) Assert.Equal(1, controller.Current.Index);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(sustainedDelay ? 4 : 0, controller.Current.Index);
        Assert.Equal(sustainedDelay ? 4 : 1, controller.Current.DownscaleFactor);
        if (!sustainedDelay) Assert.Equal(30, controller.Current.TargetFps);
    }

    [Fact]
    public void Recovery_needs_sustained_health_and_climbs_one_rung_at_a_time()
    {
        var controller = new AdaptiveQualityController(30);
        controller.Evaluate(Lossy(0.20));
        controller.Evaluate(Lossy(0.20));
        Assert.Equal(2, controller.Current.Index);

        // Not enough healthy windows yet.
        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover - 1; i++)
        {
            controller.Evaluate(Healthy);
        }

        Assert.Equal(2, controller.Current.Index);

        controller.Evaluate(Healthy);
        Assert.Equal(1, controller.Current.Index);

        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover; i++)
        {
            controller.Evaluate(Healthy);
        }

        Assert.Equal(0, controller.Current.Index);
    }

    [Fact]
    public void A_single_healthy_window_after_loss_does_not_climb_back()
    {
        var controller = new AdaptiveQualityController(30);
        controller.Evaluate(Lossy(0.20));

        controller.Evaluate(Healthy);

        Assert.Equal(1, controller.Current.Index);
    }

    [Fact]
    public void Marginal_loss_holds_the_current_level()
    {
        var controller = new AdaptiveQualityController(30);
        controller.Evaluate(Lossy(0.20));
        Assert.Equal(1, controller.Current.Index);

        // Between the recover and degrade thresholds: neither up nor down.
        for (var i = 0; i < 10; i++)
        {
            controller.Evaluate(Lossy(0.03));
        }

        Assert.Equal(1, controller.Current.Index);
    }

    [Fact]
    public void Reset_returns_to_the_top_of_the_ladder()
    {
        var controller = new AdaptiveQualityController(30);
        controller.Evaluate(Lossy(0.20));
        controller.Evaluate(Lossy(0.20));

        controller.Reset();

        Assert.Equal(0, controller.Current.Index);
    }

    [Fact]
    public void The_ladder_never_produces_an_unusable_frame_rate()
    {
        foreach (var maxFps in new[] { 1, 15, 30, 60 })
        {
            var controller = new AdaptiveQualityController(maxFps);

            Assert.All(controller.Ladder, level =>
            {
                Assert.True(level.TargetFps >= 1, $"{level} has a frame rate below 1");
                Assert.True(level.TargetFps <= maxFps);
                Assert.True(level.DownscaleFactor is 1 or 2 or 4);
            });
        }
    }
}

public class ConnectionPolicyProviderTests
{
    private readonly ConnectionPolicyProvider _policy = new();

    [Theory]
    [InlineData(0.00, 40, 5, ConnectionHealth.Excellent, ConnectionPolicyAction.Recover)]
    [InlineData(0.01, 120, 25, ConnectionHealth.Good, ConnectionPolicyAction.Recover)]
    [InlineData(0.03, 220, 45, ConnectionHealth.Fair, ConnectionPolicyAction.Hold)]
    [InlineData(0.08, 40, 5, ConnectionHealth.Poor, ConnectionPolicyAction.Degrade)]
    public void Real_measurements_map_to_deterministic_health_and_action(
        double loss,
        double rttMs,
        double jitterMs,
        ConnectionHealth expectedHealth,
        ConnectionPolicyAction expectedAction)
    {
        var decision = _policy.Evaluate(new ConnectionPolicyInput
        {
            PacketLossFraction = loss,
            QueueDropRatio = 0,
            FramesObserved = 60,
            RttMs = rttMs,
            JitterMs = jitterMs,
            AvailableOutgoingBitrateKbps = 5000,
            CurrentTargetBitrateKbps = 4000,
        });

        Assert.Equal(expectedHealth, decision.Health);
        Assert.Equal(expectedAction, decision.Action);
    }

    [Fact]
    public void Missing_measurements_are_reported_as_unknown_instead_of_inventing_health()
    {
        var decision = _policy.Evaluate(new ConnectionPolicyInput
        {
            PacketLossFraction = 0,
            QueueDropRatio = 0,
            FramesObserved = 0,
        });

        Assert.Equal(ConnectionHealth.Unknown, decision.Health);
        Assert.Equal(ConnectionPolicyAction.Hold, decision.Action);
        Assert.Equal("awaiting_measurements", decision.ReasonCode);
    }

    [Fact]
    public void Invalid_telemetry_fails_toward_lower_quality()
    {
        var decision = _policy.Evaluate(new ConnectionPolicyInput
        {
            PacketLossFraction = double.NaN,
            QueueDropRatio = 0,
            FramesObserved = 60,
        });

        Assert.Equal(ConnectionHealth.Poor, decision.Health);
        Assert.Equal(ConnectionPolicyAction.Degrade, decision.Action);
        Assert.Equal("invalid_telemetry", decision.ReasonCode);
    }

    [Theory]
    [InlineData(0, 120, 30, 30, "viewer_render_latency")]
    [InlineData(10, 0, 30, 10, "viewer_render_backpressure")]
    public void Viewer_presentation_pressure_degrades_through_the_central_policy(
        double captureToPresentP95Ms,
        double decodeToRenderP95Ms,
        double decodeFps,
        double renderFps,
        string expectedReason)
    {
        var decision = _policy.Evaluate(new ConnectionPolicyInput
        {
            PacketLossFraction = 0,
            QueueDropRatio = 0,
            FramesObserved = 60,
            ReceiverCaptureToPresentLatencyP95Ms = captureToPresentP95Ms,
            ReceiverFrameAgeClockUncertaintyMs = 1,
            ReceiverCaptureToPresentTargetMs = 75,
            ReceiverDecodeToRenderLatencyP95Ms = decodeToRenderP95Ms,
            ReceiverDecodeFps = decodeFps,
            ReceiverRenderFps = renderFps,
        });

        Assert.Equal(ConnectionHealth.Poor, decision.Health);
        Assert.Equal(ConnectionPolicyAction.Degrade, decision.Action);
        Assert.Equal(expectedReason, decision.ReasonCode);
    }

    [Fact]
    public void Uncertain_cross_device_frame_age_does_not_force_a_false_degrade()
    {
        var decision = _policy.Evaluate(new ConnectionPolicyInput
        {
            PacketLossFraction = 0,
            QueueDropRatio = 0,
            FramesObserved = 60,
            ReceiverCaptureToPresentLatencyP95Ms = 500,
            ReceiverFrameAgeClockUncertaintyMs =
                ConnectionPolicyProvider.MaximumAdaptiveFrameAgeClockUncertaintyMs + 1,
            ReceiverCaptureToPresentTargetMs = 75,
        });

        Assert.Equal(ConnectionPolicyAction.Recover, decision.Action);
        Assert.Equal("stable_headroom", decision.ReasonCode);
    }

    [Fact]
    public void Uncertain_viewer_input_latency_does_not_force_a_false_degrade()
    {
        var decision = _policy.Evaluate(new ConnectionPolicyInput
        {
            PacketLossFraction = 0,
            QueueDropRatio = 0,
            FramesObserved = 60,
            ReceiverInputToInjectionLatencyP95Ms = 500,
            ReceiverInputClockUncertaintyMs =
                ConnectionPolicyProvider.MaximumAdaptiveInputClockUncertaintyMs + 1,
        });

        Assert.Equal(ConnectionPolicyAction.Recover, decision.Action);
        Assert.Equal("stable_headroom", decision.ReasonCode);
    }

    [Fact]
    public void Transfer_modes_change_the_real_bounded_bulk_allocation()
    {
        var control = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.RemoteControlPriority);
        var balanced = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.Balanced);
        var transfer = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.FileTransferPriority);

        Assert.True(control.MaximumBulkBufferedBytes < balanced.MaximumBulkBufferedBytes);
        Assert.True(balanced.MaximumBulkBufferedBytes < transfer.MaximumBulkBufferedBytes);
        Assert.Equal(256UL * 1024, control.MaximumBulkBufferedBytes);
        Assert.InRange(transfer.MaximumBulkBufferedBytes, 1UL, 16UL * 1024 * 1024);
    }

    [Fact]
    public void Four_k_video_and_interactive_traffic_are_reserved_before_file_bandwidth()
    {
        var allocation = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.FileTransferPriority,
            new MediaStatistics
            {
                ConnectionHealth = ConnectionHealth.Excellent,
                CurrentBitrateKbps = 34_000,
                TargetBitrateKbps = 36_000,
                AvailableOutgoingBitrateKbps = 52_000,
            });

        Assert.Equal(256, allocation.InteractiveReserveKbps);
        Assert.Equal(37_800, allocation.MediaReserveKbps);
        Assert.Equal(13_944, allocation.MaximumBulkKbps);
        Assert.Equal(2UL * 1024 * 1024, allocation.MaximumBulkBufferedBytes);
    }

    [Fact]
    public void Stable_four_k_video_without_a_bandwidth_estimate_uses_a_bounded_high_speed_bulk_cap()
    {
        var statistics = new MediaStatistics
        {
            ConnectionHealth = ConnectionHealth.Excellent,
            CurrentBitrateKbps = 34_000,
            TargetBitrateKbps = 36_000,
            AvailableOutgoingBitrateKbps = 0,
        };
        var allocation = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.FileTransferPriority,
            statistics);

        Assert.Equal(50_000, ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.RemoteControlPriority,
            statistics).MaximumBulkKbps);
        Assert.Equal(100_000, ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.Balanced,
            statistics).MaximumBulkKbps);
        Assert.Equal(150_000, allocation.MaximumBulkKbps);
        Assert.Equal(256, allocation.InteractiveReserveKbps);
        Assert.Equal(37_800, allocation.MediaReserveKbps);
        Assert.Equal(2UL * 1024 * 1024, allocation.MaximumBulkBufferedBytes);
        var oneGibibyteSeconds = (1024d * 1024 * 1024 * 8) / (allocation.MaximumBulkKbps * 1000d);
        Assert.InRange(oneGibibyteSeconds, 0, 60);
    }

    [Fact]
    public void Measured_high_capacity_link_uses_spare_bandwidth_above_the_provisional_cap()
    {
        var allocation = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.FileTransferPriority,
            new MediaStatistics
            {
                ConnectionHealth = ConnectionHealth.Excellent,
                CurrentBitrateKbps = 34_000,
                TargetBitrateKbps = 36_000,
                AvailableOutgoingBitrateKbps = 250_000,
            });

        Assert.Equal(211_944, allocation.MaximumBulkKbps);
        Assert.True(allocation.MaximumBulkKbps > 150_000);
        Assert.Equal(37_800, allocation.MediaReserveKbps);
        Assert.Equal(256, allocation.InteractiveReserveKbps);
    }

    [Fact]
    public void Expired_network_feedback_does_not_raise_the_bulk_budget()
    {
        var statistics = new MediaStatistics
        {
            ConnectionHealth = ConnectionHealth.Poor,
            CurrentBitrateKbps = 4_000,
            TargetBitrateKbps = 5_000,
        };
        var poor = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.Balanced, statistics);
        var expired = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.Balanced, statistics with
            {
                ConnectionHealth = ConnectionHealth.Unknown,
                QualityChangeReason = "awaiting_network_feedback",
            });

        Assert.Equal(poor.MaximumBulkKbps, expired.MaximumBulkKbps);
        Assert.Equal(poor.MaximumBulkBufferedBytes, expired.MaximumBulkBufferedBytes);
    }

    [Fact]
    public void Poor_network_conditions_throttle_file_traffic_before_interactive_media()
    {
        var healthy = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.Balanced,
            new MediaStatistics
            {
                ConnectionHealth = ConnectionHealth.Good,
                CurrentBitrateKbps = 4_000,
                TargetBitrateKbps = 5_000,
                AvailableOutgoingBitrateKbps = 20_000,
            });
        var poor = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.Balanced,
            new MediaStatistics
            {
                ConnectionHealth = ConnectionHealth.Poor,
                CurrentBitrateKbps = 4_000,
                TargetBitrateKbps = 5_000,
                AvailableOutgoingBitrateKbps = 20_000,
            });

        Assert.True(poor.MaximumBulkKbps < healthy.MaximumBulkKbps);
        Assert.Equal(128, poor.MaximumBulkKbps);
        Assert.Equal(64UL * 1024, poor.MaximumBulkBufferedBytes);
        Assert.True(poor.InteractiveReserveKbps > 0);
        Assert.True(poor.MediaReserveKbps >= 5_000);
    }

    [Fact]
    public void Measured_input_latency_pressure_throttles_bulk_before_control()
    {
        var allocation = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.Balanced,
            new MediaStatistics
            {
                CurrentBitrateKbps = 8_000,
                TargetBitrateKbps = 8_000,
                AvailableOutgoingBitrateKbps = 20_000,
                ConnectionHealth = ConnectionHealth.Excellent,
                InputToInjectionLatencyP95Ms = 60,
                InputClockUncertaintyMs = 2,
            });

        Assert.Equal(64UL * 1024, allocation.MaximumBulkBufferedBytes);
        Assert.Equal(128, allocation.MaximumBulkKbps);
        Assert.True(allocation.InteractiveReserveKbps >= 1_024);
        Assert.Equal(8_800, allocation.MediaReserveKbps);
    }

    [Fact]
    public void Uncertain_input_latency_does_not_drive_transfer_throttling()
    {
        var allocation = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.Balanced,
            new MediaStatistics
            {
                CurrentBitrateKbps = 8_000,
                TargetBitrateKbps = 8_000,
                AvailableOutgoingBitrateKbps = 20_000,
                ConnectionHealth = ConnectionHealth.Excellent,
                InputToInjectionLatencyP95Ms = 60,
                InputClockUncertaintyMs = 25,
            });

        Assert.True(allocation.MaximumBulkKbps > 128);
        Assert.Equal(512UL * 1024, allocation.MaximumBulkBufferedBytes);
    }

    [Fact]
    public void File_only_sessions_keep_the_bounded_window_without_inventing_a_video_cap()
    {
        var allocation = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.FileTransferPriority,
            MediaStatistics.Empty);

        Assert.Equal(0, allocation.MaximumBulkKbps);
        Assert.Equal(0, allocation.MediaReserveKbps);
        Assert.True(allocation.InteractiveReserveKbps > 0);
        Assert.Equal(2UL * 1024 * 1024, allocation.MaximumBulkBufferedBytes);
    }

    [Fact]
    public void Interactive_startup_uses_a_finite_high_speed_bulk_rate_before_media_is_measured()
    {
        var allocation = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.Balanced,
            MediaStatistics.Empty,
            hasInteractiveTraffic: true);

        Assert.Equal(100_000, allocation.MaximumBulkKbps);
        Assert.True(allocation.InteractiveReserveKbps > 0);
        Assert.Equal(0, allocation.MediaReserveKbps);
        Assert.Equal(512UL * 1024, allocation.MaximumBulkBufferedBytes);
    }
}

public class FrameRateLimiterAdaptationTests
{
    [Fact]
    public void The_target_rate_can_change_mid_session()
    {
        var time = new ManualTimeProvider();
        var limiter = new FrameRateLimiter(30, time);

        Assert.True(limiter.ShouldAccept());
        time.Advance(TimeSpan.FromMilliseconds(34));
        Assert.True(limiter.ShouldAccept());

        limiter.SetTargetFps(10);
        Assert.Equal(10, limiter.TargetFps);

        // 34 ms is enough for 30 fps but not for 10 fps.
        time.Advance(TimeSpan.FromMilliseconds(34));
        Assert.False(limiter.ShouldAccept());

        time.Advance(TimeSpan.FromMilliseconds(70));
        Assert.True(limiter.ShouldAccept());
    }

    [Fact]
    public void Absurd_targets_are_clamped_when_set()
    {
        var limiter = new FrameRateLimiter(30);

        limiter.SetTargetFps(0);
        Assert.Equal(1, limiter.TargetFps);

        limiter.SetTargetFps(10_000);
        Assert.Equal(120, limiter.TargetFps);
    }
}

public class BitrateLimiterTests
{
    [Fact]
    public void It_enforces_and_can_change_the_one_second_budget()
    {
        var time = new ManualTimeProvider();
        var limiter = new BitrateLimiter(80, time); // 10 KB/s

        Assert.True(limiter.TryConsume(6_000));
        Assert.False(limiter.TryConsume(5_000));

        limiter.SetTargetKbps(160);
        Assert.True(limiter.TryConsume(5_000));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryConsume(20_000));
    }
}

public class VideoSecurityFailureBudgetTests
{
    [Fact]
    public void One_damaged_video_frame_is_recoverable_but_sustained_failures_still_fail_closed()
    {
        var budget = new VideoSecurityFailureBudget(maxConsecutiveFailures: 3);

        Assert.Equal(1, budget.RecordFailure());
        Assert.False(budget.IsExhausted);

        budget.RecordSuccess();
        Assert.Equal(1, budget.RecordFailure());
        Assert.Equal(2, budget.RecordFailure());
        Assert.False(budget.IsExhausted);
        Assert.Equal(3, budget.RecordFailure());
        Assert.True(budget.IsExhausted);
    }
}
