using PeerOnQ.Media.Pipeline;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PeerOnQ.Media.Tests;

public class AdaptiveQualityControllerTests
{
    [Theory]
    [InlineData(1920, 1080, 75)]
    [InlineData(3840, 2160, 100)]
    [InlineData(2160, 3840, 100)]
    [InlineData(7680, 1080, 100)]
    [InlineData(0, 0, 75)]
    public void Automatic_frame_age_target_uses_actual_encoded_pixels(int width, int height, double expected)
    {
        Assert.Equal(expected, ConnectionPolicyProvider.GetTargetCaptureToPresentP95Ms(
            CaptureResolution.Automatic, width, height));
    }

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

    /// <summary>A soft (viewer-side) signal acts only once it has persisted for several windows.</summary>
    private static QualityLevel EvaluateSustainedSoftSignal(AdaptiveQualityController controller, QualitySample sample)
    {
        var before = controller.Current;
        for (var i = 0; i < AdaptiveQualityController.SoftEvaluationsToDegrade - 1; i++)
        {
            controller.Evaluate(sample);
            Assert.Equal(before, controller.Current);
        }

        return controller.Evaluate(sample);
    }

    [Fact]
    public void A_clean_lan_viewer_with_noisy_soft_signals_holds_its_level_for_ten_minutes()
    {
        // 0.9.85 field test: a clean LAN D3D11 viewer has a steady ~60 ms presenter delay with
        // render keeping pace with decode, and an occasional frame-age p95 just above target
        // (a key frame, a busy encoder). Neither may walk the session down the ladder.
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(
            maxFps: 30, maxBitrateKbps: 6000, timeProvider: time, highMotionFps: 60);
        var changes = 0;
        controller.LevelChanged += (_, _) => changes++;
        var start = controller.Current.Index;
        var secondsNearStart = 0;

        for (var second = 0; second < 600; second++)
        {
            var spike = second % 7 == 0 || second % 11 == 0;
            controller.Evaluate(new QualitySample
            {
                NetworkFeedbackAvailable = true,
                PacketLossFraction = 0,
                RttMs = 2,
                BaselineRttMs = 1,
                JitterMs = 1,
                QueueDrops = 0,
                FramesEncoded = 30,
                ReceiverFeedbackAvailable = true,
                ReceiverDecodeFps = 30,
                ReceiverRenderFps = second % 13 == 0 ? 28 : 30,
                ReceiverDecodeToRenderLatencyP95Ms = 60,
                ReceiverCaptureToPresentLatencyP95Ms = spike ? 117 : 70,
                ReceiverFrameAgeClockUncertaintyMs = 2,
                ReceiverCaptureToPresentTargetMs = 108,
            });
            if (controller.Current.Index - start is 0 or 1) secondsNearStart++;
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(changes < 10, $"{changes} level changes in ten minutes");
        Assert.True(secondsNearStart >= 540, $"only {secondsNearStart} s at the start level or one below");
        Assert.Equal(1, controller.Current.DownscaleFactor);
    }

    [Fact]
    public void A_paced_presenter_with_constant_render_delay_is_not_render_pressure()
    {
        var policy = new ConnectionPolicyProvider();
        var paced = new ConnectionPolicyInput
        {
            PacketLossFraction = 0,
            QueueDropRatio = 0,
            FramesObserved = 30,
            ReceiverDecodeFps = 30,
            ReceiverRenderFps = 30,
            ReceiverDecodeToRenderLatencyP95Ms = 120,
        };

        Assert.Equal(ConnectionPolicyAction.Recover, policy.Evaluate(paced).Action);

        // The same delay at a low decode rate is under three frame intervals (3 x 100 ms).
        Assert.Equal(ConnectionPolicyAction.Recover, policy.Evaluate(paced with
        {
            ReceiverDecodeFps = 10,
            ReceiverRenderFps = 8.5,
        }).Action);

        // Render falling behind decode while the delay grows is pressure.
        Assert.Equal("viewer_render_latency", policy.Evaluate(paced with { ReceiverRenderFps = 26 }).ReasonCode);
    }

    [Theory]
    [InlineData(2, 30, 2)]
    [InlineData(45, 30, 30)]
    [InlineData(0, 30, 30)]
    [InlineData(double.NaN, 30, 30)]
    [InlineData(0.2, 30, 1)]
    [InlineData(12, 0, 1)]
    public void Frame_age_allowance_uses_the_measured_capture_interval(
        double measuredFps, int targetFps, double expected)
    {
        Assert.Equal(expected, WebRtcMediaSession.MeasuredFrameRate(measuredFps, targetFps));
    }

    [Fact]
    public void A_static_screen_captured_below_the_level_rate_does_not_read_as_late_frames()
    {
        // Two captured frames per second: each may wait up to 500 ms for its slot, so a 400 ms
        // frame-age p95 is the capture cadence, not a late pipeline (0.9.85 degraded on it).
        var policy = new ConnectionPolicyProvider();
        var target = ConnectionPolicyProvider.TargetCaptureToPresentP95Ms
                     + 1000.0 / WebRtcMediaSession.MeasuredFrameRate(2, 30);

        var decision = policy.Evaluate(new ConnectionPolicyInput
        {
            PacketLossFraction = 0,
            QueueDropRatio = 0,
            FramesObserved = 2,
            ReceiverCaptureToPresentLatencyP95Ms = 400,
            ReceiverFrameAgeClockUncertaintyMs = 2,
            ReceiverCaptureToPresentTargetMs = target,
        });

        Assert.Equal(ConnectionPolicyAction.Recover, decision.Action);
    }

    [Fact]
    public void A_capture_restart_reports_its_own_reason_instead_of_the_old_sources_decision()
    {
        var controller = new AdaptiveQualityController(maxFps: 30);
        EvaluateSustainedSoftSignal(controller, Healthy with
        {
            ReceiverCaptureToPresentLatencyP95Ms = 150,
            ReceiverFrameAgeClockUncertaintyMs = 2,
            ReceiverCaptureToPresentTargetMs = 108,
        });
        Assert.Equal("viewer_frame_age", controller.LastDecision.ReasonCode);

        controller.Reset();

        Assert.Equal(0, controller.Current.Index);
        Assert.Equal(AdaptiveQualityController.CaptureRestartReason, controller.LastDecision.ReasonCode);
    }

    [Fact]
    public async Task A_display_switch_that_keeps_the_level_is_logged_as_a_reapply_not_a_change()
    {
        // 0.9.85 logged every display switch as "quality L1 (viewer_frame_age ...)", which read as
        // L1 -> L1 changes one second apart. Only a real change may use the change message.
        var log = new CapturingLogger();
        await using var sharer = WebRtcMediaSession.CreateSharer(
            SessionId.New(), capture: null, MediaProfile.For(QualityProfile.Quality), log);
        var adaptive = sharer.Adaptive!;

        adaptive.Evaluate(Lossy(0.20));
        sharer.RestartQuality();
        sharer.RestartQuality();
        sharer.RestartQuality();

        var changes = log.Messages.Where(m => m.Contains(" quality L", StringComparison.Ordinal)
                                              && !m.Contains("re-applied", StringComparison.Ordinal)).ToList();
        var reapplied = log.Messages.Where(m => m.Contains("re-applied", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Contains("packet_loss", changes[0], StringComparison.Ordinal);
        Assert.Contains(AdaptiveQualityController.CaptureRestartReason, changes[1], StringComparison.Ordinal);
        Assert.Equal(2, reapplied.Count);
        Assert.All(reapplied, m => Assert.Contains(AdaptiveQualityController.CaptureRestartReason, m, StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_gate) return [.. _messages]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _messages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void A_soft_signal_interrupted_by_healthy_windows_never_degrades()
    {
        var controller = new AdaptiveQualityController(maxFps: 30);
        var late = Healthy with
        {
            ReceiverInputToInjectionLatencyP95Ms = 80,
            ReceiverInputClockUncertaintyMs = 2,
        };

        // Two late windows at a time, each burst ended by one healthy window.
        const int lateWindows = 2;
        Assert.True(lateWindows < AdaptiveQualityController.SoftEvaluationsToDegrade);
        for (var i = 0; i < 30; i++)
        {
            for (var j = 0; j < lateWindows; j++)
                controller.Evaluate(late);
            controller.Evaluate(Healthy);
        }

        Assert.Equal(0, controller.Current.Index);
    }

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

    [Theory]
    [InlineData(QualityProfile.Automatic, CaptureResolution.Automatic)]
    [InlineData(QualityProfile.Quality, CaptureResolution.P2160)]
    [InlineData(QualityProfile.Balanced, CaptureResolution.P2160)]
    [InlineData(QualityProfile.Performance, CaptureResolution.Native)]
    public async Task Source_detail_choices_keep_pixels_while_latency_qos_can_reduce_fps_and_bitrate(
        QualityProfile quality, CaptureResolution resolution)
    {
        var profile = MediaProfile.For(quality) with { Resolution = resolution };
        await using var session = WebRtcMediaSession.CreateSharer(SessionId.New(), capture: null, profile);
        var ladder = Assert.IsType<AdaptiveQualityController>(session.Adaptive).Ladder;
        Assert.All(ladder, level => Assert.Equal(1, level.DownscaleFactor));
        Assert.True(ladder[^1].TargetFps < profile.TargetFps);
        Assert.True(ladder[^1].MaxBitrateKbps < profile.MaxBitrateKbps);
    }

    [Fact]
    public void Ultra_hd_ladder_gives_up_4k_before_frame_rate()
    {
        var controller = new AdaptiveQualityController(30, maxBitrateKbps: 6_000);
        var changes = new List<QualityLevel>();
        controller.LevelChanged += (_, level) => changes.Add(level);

        controller.SetUltraHdSource(true);

        Assert.True(controller.IsUltraHdLadder);
        var ladder = controller.Ladder;
        Assert.Equal((30, 1, 15_000), (ladder[0].TargetFps, ladder[0].DownscaleFactor, ladder[0].MaxBitrateKbps));
        Assert.Equal((30, 2, 6_000), (ladder[1].TargetFps, ladder[1].DownscaleFactor, ladder[1].MaxBitrateKbps));
        Assert.All(ladder.Skip(1), level => Assert.Equal(2, level.DownscaleFactor));
        Assert.True(ladder[^1].TargetFps < ladder[1].TargetFps);
        Assert.Equal(15_000, Assert.Single(changes).MaxBitrateKbps);

        // The first congested window leaves 4K immediately while keeping 30 fps.
        var level = controller.Evaluate(Lossy(0.10));
        Assert.Equal(1, level.Index);
        Assert.Equal(30, level.TargetFps);
        Assert.Equal(2, level.DownscaleFactor);
    }

    [Fact]
    public void High_motion_rung_is_earned_only_while_allowed_and_left_when_withdrawn()
    {
        var controller = new AdaptiveQualityController(30, maxBitrateKbps: 6_000, highMotionFps: 60);

        Assert.True(controller.HasHighMotionRung);
        var top = controller.Ladder[0];
        Assert.Equal((60, 1, 9_000), (top.TargetFps, top.DownscaleFactor, top.MaxBitrateKbps));
        Assert.Equal((1, 30), (controller.Current.Index, controller.Current.TargetFps));

        // Healthy windows alone do not unlock 60 fps: the encoder must be able to sustain it.
        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover * 2; i++)
            controller.Evaluate(Healthy);
        Assert.Equal(30, controller.Current.TargetFps);

        controller.SetHighMotionAllowed(true);
        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover; i++)
            controller.Evaluate(Healthy);
        Assert.Equal(60, controller.Current.TargetFps);

        var changes = new List<QualityLevel>();
        controller.LevelChanged += (_, level) => changes.Add(level);
        controller.SetHighMotionAllowed(false);
        Assert.Equal(30, controller.Current.TargetFps);
        Assert.Equal(30, Assert.Single(changes).TargetFps);
    }

    [Fact]
    public void High_motion_steps_down_to_the_profile_rate_first_and_4k_sources_never_use_it()
    {
        var controller = new AdaptiveQualityController(30, maxBitrateKbps: 6_000, highMotionFps: 60);
        controller.SetHighMotionAllowed(true);
        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover; i++)
            controller.Evaluate(Healthy);
        Assert.Equal(0, controller.Current.Index);

        var level = controller.Evaluate(Lossy(0.10));
        Assert.Equal((30, 1, 6_000), (level.TargetFps, level.DownscaleFactor, level.MaxBitrateKbps));

        controller.SetUltraHdSource(true);
        Assert.Equal((0, 30, 1), (controller.Current.Index, controller.Current.TargetFps, controller.Current.DownscaleFactor));
        Assert.All(controller.Ladder, rung => Assert.True(rung.TargetFps <= 30));

        controller.SetUltraHdSource(false);
        Assert.Equal((1, 30), (controller.Current.Index, controller.Current.TargetFps));
    }

    [Fact]
    public void Automatic_profile_runs_at_60_fps_and_offers_120_fps_only_as_an_earned_high_motion_rung()
    {
        var profile = MediaProfile.For(QualityProfile.Automatic);
        Assert.Equal((60, 120), (profile.TargetFps, profile.HighMotionFps));
        Assert.Equal(MediaProfile.AutomaticBitrateKbps, profile.MaxBitrateKbps);
        Assert.Equal(0, MediaProfile.For(QualityProfile.Performance).HighMotionFps);
        Assert.False(new AdaptiveQualityController(60, highMotionFps: 60).HasHighMotionRung);
        var controller = new AdaptiveQualityController(profile.TargetFps, profile.MaxBitrateKbps, highMotionFps: profile.HighMotionFps);
        Assert.True(controller.HasHighMotionRung);
        Assert.Equal(60, controller.Current.TargetFps);
        Assert.Equal(120, controller.Ladder[0].TargetFps);
    }

    [Theory]
    [InlineData(null, 60, 0)]
    [InlineData(60, 60, 0)]
    [InlineData(59, 59, 0)]
    [InlineData(75, 60, 75)]
    [InlineData(120, 60, 120)]
    [InlineData(144, 60, 120)]
    [InlineData(240, 60, 120)]
    [InlineData(30, 30, 0)]
    public void Automatic_frame_rates_follow_the_viewer_display(int? viewerHz, int expectedFps, int expectedHighMotion)
    {
        var profile = MediaProfile.For(QualityProfile.Automatic).ForViewerDisplay(viewerHz);
        Assert.Equal((expectedFps, expectedHighMotion), (profile.TargetFps, profile.HighMotionFps));
        // Fixed profiles keep their own rate.
        Assert.Equal(MediaProfile.For(QualityProfile.Office), MediaProfile.For(QualityProfile.Office).ForViewerDisplay(viewerHz));
    }

    [Fact]
    public void A_software_limited_sharer_keeps_the_0_9_87_rate_and_ceiling()
    {
        var limited = MediaProfile.For(QualityProfile.Automatic).ForViewerDisplay(144).ForSoftwareLimitedSharer();
        Assert.Equal((30, 0, 6000), (limited.TargetFps, limited.HighMotionFps, limited.MaxBitrateKbps));
    }

    [Fact]
    public void The_native_4k_rung_stays_at_30_fps_when_the_profile_runs_at_60()
    {
        var controller = new AdaptiveQualityController(60, MediaProfile.AutomaticBitrateKbps, highMotionFps: 120);
        controller.SetUltraHdSource(true);
        Assert.Equal((0, 30, 1), (controller.Current.Index, controller.Current.TargetFps, controller.Current.DownscaleFactor));
        Assert.Equal(60, controller.Ladder[1].TargetFps);
    }

    [Fact]
    public void A_failed_4k_attempt_doubles_the_retry_dwell_and_stable_4k_resets_it()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(30, timeProvider: time);
        controller.SetUltraHdSource(true);

        controller.Evaluate(Lossy(0.10));
        Assert.Equal(1, controller.Current.Index);
        Assert.Equal(TimeSpan.FromSeconds(20), controller.UltraHdRetryDwell);

        // Healthy windows before the dwell keep the standard tier.
        time.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover; i++)
            controller.Evaluate(Healthy);
        Assert.Equal(1, controller.Current.Index);

        time.Advance(TimeSpan.FromSeconds(10));
        controller.Evaluate(Healthy);
        Assert.Equal(0, controller.Current.Index);

        // This time 4K holds for a minute, so the next fallback restarts at the minimum dwell.
        time.Advance(AdaptiveQualityController.UltraHdStableWindow);
        controller.Evaluate(Lossy(0.10));
        Assert.Equal(1, controller.Current.Index);
        Assert.Equal(AdaptiveQualityController.MinimumResolutionDwell, controller.UltraHdRetryDwell);
    }

    [Fact]
    public void Repeated_4k_failures_cap_the_retry_dwell()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(30, timeProvider: time);
        controller.SetUltraHdSource(true);

        for (var round = 0; round < 12; round++)
        {
            controller.Evaluate(Lossy(0.10));
            Assert.Equal(1, controller.Current.Index);
            time.Advance(controller.UltraHdRetryDwell);
            for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover; i++)
                controller.Evaluate(Healthy);
            Assert.Equal(0, controller.Current.Index);
        }

        Assert.Equal(AdaptiveQualityController.MaximumUltraHdRetryDwell, controller.UltraHdRetryDwell);
    }

    [Fact]
    public void Switching_between_4k_and_standard_sources_keeps_the_equivalent_quality()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(30, timeProvider: time);
        controller.Evaluate(Lossy(0.10));
        Assert.Equal(1, controller.Current.Index);

        controller.SetUltraHdSource(true);
        Assert.Equal(2, controller.Current.Index);
        Assert.Equal(20, controller.Current.TargetFps);
        Assert.Equal(2, controller.Current.DownscaleFactor);

        controller.SetUltraHdSource(false);
        Assert.False(controller.IsUltraHdLadder);
        Assert.Equal(1, controller.Current.Index);
        Assert.Equal(20, controller.Current.TargetFps);
        Assert.Equal(1, controller.Current.DownscaleFactor);
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
        // Half scale is the floor: the last rung lowers rate, never to an unreadable quarter.
        Assert.Equal(2, ladder[4].DownscaleFactor);
        Assert.True(ladder[4].TargetFps < ladder[3].TargetFps);
    }

    [Fact]
    public async Task High_quality_ladder_preserves_the_requested_resolution()
    {
        var profile = MediaProfile.For(QualityProfile.Quality);
        await using var session = WebRtcMediaSession.CreateSharer(SessionId.New(), capture: null, profile);
        var controller = Assert.IsType<AdaptiveQualityController>(session.Adaptive);

        Assert.Equal(CaptureResolution.P1080, profile.Resolution);
        Assert.Equal(12_000, controller.Current.MaxBitrateKbps);
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
        var delayed = Healthy with
        {
            RttMs = 120,
            BaselineRttMs = 120,
            ReceiverInputToInjectionLatencyP95Ms = inputLatencyMs,
            ReceiverInputClockUncertaintyMs = 2,
            ReceiverCaptureToPresentLatencyP95Ms = frameAgeMs,
            ReceiverFrameAgeClockUncertaintyMs = 2,
        };

        // Viewer-side delay is a soft signal: it must persist before it reduces production.
        EvaluateSustainedSoftSignal(controller, delayed);

        Assert.Equal(1, controller.Current.Index);
        Assert.Equal(expectedReason, controller.LastDecision.ReasonCode);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    public void Higher_rtt_does_not_hide_congestion_behind_a_larger_transit_allowance(double baselineRttMs)
    {
        var controller = new AdaptiveQualityController();
        EvaluateSustainedSoftSignal(controller, Healthy with
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

        // One resolution step (to the half-scale floor) means no dwell can hold the descent.
        for (var i = 0; i < 4; i++)
            controller.Evaluate(Lossy(0.20));
        Assert.Equal(controller.Ladder.Count - 1, controller.Current.Index);
        time.Advance(AdaptiveQualityController.MinimumResolutionDwell);

        for (var i = 0; i < 6; i++)
            controller.Evaluate(Lossy(0.20));

        Assert.Equal(controller.Ladder.Count - 1, controller.Current.Index);
        Assert.Equal(2, controller.Current.DownscaleFactor);

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

        // The bottom rung lowers rate at the same half scale.
        controller.Evaluate(Lossy(0.20));
        Assert.Equal((4, 2), (controller.Current.Index, controller.Current.DownscaleFactor));

        // Recovery inside the dwell may raise rate but not resolution.
        for (var i = 0; i < AdaptiveQualityController.HealthyEvaluationsToRecover * 2; i++)
            controller.Evaluate(Healthy);
        Assert.Equal((3, 2), (controller.Current.Index, controller.Current.DownscaleFactor));

        time.Advance(AdaptiveQualityController.MinimumResolutionDwell);
        controller.Evaluate(Healthy);
        Assert.Equal(1, controller.Current.DownscaleFactor);
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
    public void High_measured_rtt_degrades_at_once_and_sustained_high_jitter_lowers_only_the_rate()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(30, timeProvider: time);

        var highRtt = controller.Evaluate(new QualitySample
        {
            PacketLossFraction = 0,
            QueueDrops = 0,
            FramesEncoded = 60,
            RttMs = AdaptiveQualityController.DegradeRttMs + 1,
        });
        Assert.Equal(1, highRtt.Index);

        // Dense 30 fps frames: jitter above the threshold is real pressure, but a soft one.
        var highJitter = new QualitySample
        {
            PacketLossFraction = 0,
            QueueDrops = 0,
            FramesEncoded = 30,
            JitterMs = AdaptiveQualityController.DegradeJitterMs + 1,
            SenderFrameRateFps = 30,
        };
        time.Advance(AdaptiveQualityController.SoftDegradeDwell);
        Assert.Equal(2, EvaluateSustainedSoftSignal(controller, highJitter).Index);
        Assert.Equal("high_jitter", controller.LastDecision.ReasonCode);

        // Never fewer pixels for jitter alone: L3 -> L4 would halve the scale.
        for (var second = 0; second < 120; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            controller.Evaluate(highJitter);
        }
        Assert.Equal(1, controller.Current.DownscaleFactor);
    }

    /// <summary>
    /// 0.9.86 field test (LNK-157 sharing a static desktop to LNK-363 on one Wi-Fi /24): capture
    /// at 2-2.5 fps, jitter 35-88 ms, rtt 4-8 ms, no loss. high_jitter took L1 to L5 (half scale)
    /// within 3 s, three times in one session.
    /// </summary>
    [Fact]
    public void A_static_screen_on_a_clean_lan_stays_at_l1_despite_sparse_frame_jitter()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(maxFps: 30, maxBitrateKbps: 6000, timeProvider: time);
        var changes = 0;
        controller.LevelChanged += (_, _) => changes++;
        double[] fieldJitter = [15, 72, 12, 6, 22, 35, 88, 11, 4, 63, 41, 9];

        for (var second = 0; second < 600; second++)
        {
            controller.Evaluate(new QualitySample
            {
                NetworkFeedbackAvailable = true,
                PacketLossFraction = 0,
                RttMs = 4 + (second % 5),
                BaselineRttMs = 3,
                JitterMs = fieldJitter[second % fieldJitter.Length],
                SenderFrameRateFps = second % 3 == 0 ? 2.5 : 2,
                QueueDrops = 0,
                FramesEncoded = 2,
            });
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(0, changes);
        Assert.Equal(0, controller.Current.Index);
        Assert.Equal(1, controller.Current.DownscaleFactor);
    }

    [Theory]
    [InlineData(88, 2, 14.08)]
    [InlineData(88, 30, 88)]
    [InlineData(100, 10, 80)]
    [InlineData(40, 12.5, 40)]
    [InlineData(60, 0, 60)]
    public void Jitter_is_judged_against_the_measured_frame_interval(
        double jitterMs, double senderFps, double expectedMs)
    {
        Assert.Equal(expectedMs, ConnectionPolicyProvider.EffectiveJitterMs(jitterMs, senderFps), 2);
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

        var level = EvaluateSustainedSoftSignal(controller, new QualitySample
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

        var level = EvaluateSustainedSoftSignal(controller, new QualitySample
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

    [Fact]
    public void Late_frames_with_prompt_rendering_lower_rate_but_keep_full_resolution()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(timeProvider: time);
        for (var second = 0; second < 30; second++)
        {
            controller.Evaluate(new QualitySample
            {
                NetworkFeedbackAvailable = true,
                PacketLossFraction = 0,
                RttMs = 40,
                QueueDrops = 0,
                FramesEncoded = 30,
                ReceiverFeedbackAvailable = true,
                ReceiverDecodeFps = 30,
                ReceiverRenderFps = 30,
                ReceiverDecodeToRenderLatencyP95Ms = 8,
                ReceiverCaptureToPresentLatencyP95Ms = 220,
                ReceiverFrameAgeClockUncertaintyMs = 2,
            });
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal("viewer_frame_age", controller.LastDecision.ReasonCode);
        Assert.Equal(1, controller.Current.DownscaleFactor);
        Assert.True(controller.Current.TargetFps < 30);
    }

    /// <summary>
    /// Field LAN (0% loss, viewer decoding and rendering at the encoder rate): frame age took 1080p from
    /// L2 to L5 (10 fps, half scale) in 7 s. Frame age alone now steps the rate once per dwell
    /// and never the scale; packet loss still degrades at once.
    /// </summary>
    [Fact]
    public void Frame_age_alone_steps_one_rate_level_per_dwell_and_never_lowers_the_scale()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(timeProvider: time);
        var start = controller.Current;
        var changes = new List<(TimeSpan At, QualityLevel Level)>();
        var elapsed = TimeSpan.Zero;
        controller.LevelChanged += (_, level) => changes.Add((elapsed, level));

        for (var second = 0; second < 25; second++)
        {
            controller.Evaluate(new QualitySample
            {
                NetworkFeedbackAvailable = true,
                PacketLossFraction = 0,
                RttMs = 10,
                QueueDrops = 0,
                FramesEncoded = 10,
                ReceiverFeedbackAvailable = true,
                ReceiverDecodeFps = 10,
                ReceiverRenderFps = 10,
                ReceiverDecodeToRenderLatencyP95Ms = 8,
                ReceiverCaptureToPresentLatencyP95Ms = 400,
                ReceiverFrameAgeClockUncertaintyMs = 2,
            });
            time.Advance(TimeSpan.FromSeconds(1));
            elapsed += TimeSpan.FromSeconds(1);
        }

        Assert.Equal("viewer_frame_age", controller.LastDecision.ReasonCode);
        Assert.Equal(start.DownscaleFactor, controller.Current.DownscaleFactor);
        Assert.InRange(changes.Count, 1, 3);
        for (var index = 1; index < changes.Count; index++)
            Assert.True(changes[index].At - changes[index - 1].At >= AdaptiveQualityController.FrameAgeDegradeDwell);

        // Real loss is not a soft signal.
        var before = controller.Current.Index;
        controller.Evaluate(new QualitySample
        {
            NetworkFeedbackAvailable = true,
            PacketLossFraction = 0.2,
            RttMs = 10,
            QueueDrops = 0,
            FramesEncoded = 10,
            ReceiverFeedbackAvailable = true,
            ReceiverDecodeFps = 10,
            ReceiverRenderFps = 10,
        });
        Assert.True(controller.Current.Index > before);
    }

    /// <summary>
    /// Field 0.9.84 (i5-5300U viewer, software VP8, bitmap presenter): render lagged decode only
    /// at 30 fps, yet the sharer sat at L5 66% of the time and made 168 changes, reverting every
    /// step up within 1-2 s on samples from the previous level.
    /// </summary>
    [Fact]
    public void A_viewer_that_lags_only_at_30_fps_settles_at_full_scale_without_flapping()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(timeProvider: time);
        var changes = new List<TimeSpan>();
        var elapsed = TimeSpan.Zero;
        controller.LevelChanged += (_, level) =>
        {
            changes.Add(elapsed);
            Assert.Equal(1, level.DownscaleFactor);
        };

        for (var second = 0; second < 300; second++)
        {
            var lagging = controller.Current.TargetFps >= 30;
            controller.Evaluate(new QualitySample
            {
                NetworkFeedbackAvailable = true,
                PacketLossFraction = 0,
                RttMs = 8,
                QueueDrops = 0,
                FramesEncoded = controller.Current.TargetFps,
                ReceiverFeedbackAvailable = true,
                ReceiverDecodeFps = controller.Current.TargetFps,
                ReceiverRenderFps = lagging ? controller.Current.TargetFps * 0.7 : controller.Current.TargetFps,
                ReceiverDecodeToRenderLatencyP95Ms = lagging ? 90 : 12,
            });
            time.Advance(TimeSpan.FromSeconds(1));
            elapsed += TimeSpan.FromSeconds(1);
        }

        Assert.Equal(1, controller.Current.DownscaleFactor);
        Assert.True(controller.Current.TargetFps >= 20, $"settled at {controller.Current}");
        // Each failed retry doubles the wait: the last three minutes hold almost still.
        Assert.True(changes.Count(at => at >= TimeSpan.FromMinutes(2)) <= 4,
            $"changes at {string.Join(", ", changes.Select(at => at.TotalSeconds))} s");
    }

    [Fact]
    public void Nothing_encoded_is_no_evidence_of_headroom()
    {
        var time = new ManualTimeProvider();
        var controller = new AdaptiveQualityController(timeProvider: time);
        for (var i = 0; i < 2; i++)
        {
            controller.Evaluate(new QualitySample
            {
                NetworkFeedbackAvailable = true,
                PacketLossFraction = 0.2,
                RttMs = 10,
                QueueDrops = 0,
                FramesEncoded = 30,
                ReceiverFeedbackAvailable = true,
                ReceiverDecodeFps = 30,
                ReceiverRenderFps = 30,
            });
            time.Advance(TimeSpan.FromSeconds(1));
        }
        var degraded = controller.Current.Index;

        // A stalled capture: a healthy link, but nothing captured or encoded for 30 s.
        for (var second = 0; second < 30; second++)
        {
            controller.Evaluate(new QualitySample
            {
                NetworkFeedbackAvailable = true,
                PacketLossFraction = 0,
                RttMs = 10,
                QueueDrops = 0,
                FramesEncoded = 0,
                ReceiverFeedbackAvailable = true,
            });
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(degraded, controller.Current.Index);

        for (var second = 0; second < 30; second++)
        {
            controller.Evaluate(new QualitySample
            {
                NetworkFeedbackAvailable = true,
                PacketLossFraction = 0,
                RttMs = 10,
                QueueDrops = 0,
                FramesEncoded = 30,
                ReceiverFeedbackAvailable = true,
                ReceiverDecodeFps = 30,
                ReceiverRenderFps = 30,
            });
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.True(controller.Current.Index < degraded);
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
                FramesEncoded = 30,
                ReceiverDecodeFps = 30,
                ReceiverRenderFps = 30,
                ReceiverFeedbackAvailable = true,
                ReceiverDecodeToRenderLatencyP95Ms = feedback.DecodeToRenderLatencyP95Ms,
                ReceiverCaptureToPresentLatencyP95Ms = feedback.CaptureToPresentLatencyP95Ms,
                ReceiverFrameAgeClockUncertaintyMs = feedback.FrameAgeClockUncertaintyMs,
            });
            if (second == AdaptiveQualityController.SoftEvaluationsToDegrade - 1)
                Assert.Equal(1, controller.Current.Index);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        // Sustained slow presentation settles at a lower frame rate at full scale (soft signals
        // never halve a readable desktop); expired pressure recovers to the start level.
        Assert.Equal(sustainedDelay ? 2 : 0, controller.Current.Index);
        Assert.Equal(1, controller.Current.DownscaleFactor);
        Assert.Equal(sustainedDelay ? 15 : 30, controller.Current.TargetFps);
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
    // Render at 25 of 30 decoded fps: behind decode (< 0.9x), not yet backpressure (< 0.75x).
    [InlineData(0, 120, 30, 25, "viewer_render_latency")]
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

    [Theory]
    [InlineData(20, 20, 150_000)]
    [InlineData(20, 60, 150_000)]
    [InlineData(20, 75, 1_024)]
    [InlineData(20, 180, 256)]
    [InlineData(0, 400, 150_000)]
    public void Measured_queueing_delay_yields_bulk_before_video_and_input(
        double baselineRttMs, double rttMs, int expectedBulkKbps)
    {
        var allocation = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.FileTransferPriority,
            new MediaStatistics
            {
                ConnectionHealth = ConnectionHealth.Excellent,
                CurrentBitrateKbps = 14_000,
                TargetBitrateKbps = 15_000,
                RttMs = rttMs,
                BaselineRttMs = baselineRttMs,
            });

        Assert.Equal(expectedBulkKbps, allocation.MaximumBulkKbps);
        // Video and interactive reservations are never reduced to make room for bulk data.
        Assert.Equal(15_750, allocation.MediaReserveKbps);
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
