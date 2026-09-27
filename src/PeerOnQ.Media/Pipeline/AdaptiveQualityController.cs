using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Media.Pipeline;

/// <summary>One rung of the quality ladder: a frame rate and a capture downscale factor.</summary>
public readonly record struct QualityLevel(
    int Index,
    int TargetFps,
    int DownscaleFactor,
    int MaxBitrateKbps = 4000)
{
    public override string ToString() =>
        $"L{Index} ({TargetFps} fps, 1/{DownscaleFactor} scale, {MaxBitrateKbps} kbps)";
}

/// <summary>What the controller observed since the previous evaluation.</summary>
public readonly record struct QualitySample()
{
    /// <summary>Fraction of RTP packets the receiver reported lost, 0..1 (from RTCP).</summary>
    public required double PacketLossFraction { get; init; }

    /// <summary>Frames the capture queue had to discard because the encoder fell behind.</summary>
    public required long QueueDrops { get; init; }

    /// <summary>Frames the encoder produced in the same window.</summary>
    public required long FramesEncoded { get; init; }

    public bool NetworkFeedbackAvailable { get; init; } = true;
    public double RttMs { get; init; }
    public double BaselineRttMs { get; init; }
    public double JitterMs { get; init; }
    public double AvailableOutgoingBitrateKbps { get; init; }
    public bool ReceiverFeedbackAvailable { get; init; }
    public double ReceiverDecodeFps { get; init; }
    public double ReceiverRenderFps { get; init; }
    public double ReceiverDecodeToRenderLatencyP95Ms { get; init; }
    public double ReceiverCaptureToPresentLatencyP95Ms { get; init; }
    public double ReceiverFrameAgeClockUncertaintyMs { get; init; }
    public double ReceiverCaptureToPresentTargetMs { get; init; }
    public double ReceiverInputToInjectionLatencyP95Ms { get; init; }
    public double ReceiverInputClockUncertaintyMs { get; init; }
}

/// <summary>
/// Adjusts frame rate and resolution from real feedback: RTCP loss reported by the viewer and
/// local backpressure from the capture queue.
///
/// It steps down quickly (one rung per evaluation while the link is unhappy) and climbs back
/// slowly, so a brief spike does not oscillate the picture.
/// </summary>
public sealed class AdaptiveQualityController
{
    /// <summary>Loss above this means the network cannot carry the current bitrate.</summary>
    public const double DegradeLossFraction = ConnectionPolicyProvider.DegradeLossFraction;

    /// <summary>Loss below this, sustained, means there is room to improve.</summary>
    public const double RecoverLossFraction = ConnectionPolicyProvider.RecoverLossFraction;

    /// <summary>Dropping more than this share of frames locally means the encoder is behind.</summary>
    public const double DegradeDropRatio = ConnectionPolicyProvider.DegradeDropRatio;

    public const double DegradeRttMs = ConnectionPolicyProvider.DegradeRttMs;
    public const double DegradeJitterMs = ConnectionPolicyProvider.DegradeJitterMs;

    /// <summary>Consecutive healthy evaluations required before climbing one rung.</summary>
    public const int HealthyEvaluationsToRecover = 4;

    /// <summary>Fresh zero-presentation windows required before declaring a receiver stall.</summary>
    public const int ReceiverStallEvaluationsToDegrade = 2;

    /// <summary>Minimum time between resolution changes; FPS-only rungs remain responsive.</summary>
    public static readonly TimeSpan MinimumResolutionDwell = TimeSpan.FromSeconds(10);

    private readonly QualityLevel[] _ladder;
    private readonly TimeProvider _time;
    private readonly IConnectionPolicyProvider _policy;
    private readonly Lock _gate = new();
    private int _healthyStreak;
    private int _receiverStallStreak;
    private DateTimeOffset? _lastResolutionChange;

    public AdaptiveQualityController(
        int maxFps = 30,
        int maxBitrateKbps = 4000,
        TimeProvider? timeProvider = null,
        IConnectionPolicyProvider? policy = null,
        bool allowResolutionDownscale = true)
    {
        _time = timeProvider ?? TimeProvider.System;
        _policy = policy ?? new ConnectionPolicyProvider();
        var top = Math.Clamp(maxFps, 1, 120);

        // Reduce frame rate first (cheapest quality loss), then resolution. A user-selected
        // high-quality session keeps its requested pixels and trades frame rate for clarity.
        var firstResolutionDownscale = allowResolutionDownscale ? 2 : 1;
        var finalResolutionDownscale = allowResolutionDownscale ? 4 : 1;
        _ladder =
        [
            new QualityLevel(0, top, 1, Math.Max(250, maxBitrateKbps)),
            new QualityLevel(1, Math.Max(1, top * 2 / 3), 1, Math.Max(250, maxBitrateKbps * 3 / 4)),
            new QualityLevel(2, Math.Max(1, top / 2), 1, Math.Max(250, maxBitrateKbps / 2)),
            new QualityLevel(3, Math.Max(1, top / 2), firstResolutionDownscale, Math.Max(250, maxBitrateKbps / 3)),
            new QualityLevel(4, Math.Max(1, top / 3), finalResolutionDownscale, Math.Max(250, maxBitrateKbps / 5)),
        ];

        Current = _ladder[0];
    }

    public QualityLevel Current { get; private set; }

    public ConnectionPolicyDecision LastDecision { get; private set; } = new(
        ConnectionHealth.Unknown,
        ConnectionPolicyAction.Hold,
        "awaiting_measurements");

    public bool IsReceiverPresentationStalled
    {
        get
        {
            lock (_gate) return _receiverStallStreak >= ReceiverStallEvaluationsToDegrade;
        }
    }

    public IReadOnlyList<QualityLevel> Ladder => _ladder;

    /// <summary>Fires only when the level actually changes.</summary>
    public event EventHandler<QualityLevel>? LevelChanged;

    public QualityLevel Evaluate(QualitySample sample)
    {
        QualityLevel? changed = null;

        lock (_gate)
        {
            var total = sample.FramesEncoded + sample.QueueDrops;
            var dropRatio = total > 0 ? (double)sample.QueueDrops / total : 0;
            var receiverStalledThisWindow = sample.ReceiverFeedbackAvailable
                                            && sample.FramesEncoded > 0
                                            && sample.ReceiverDecodeFps == 0
                                            && sample.ReceiverRenderFps == 0;
            _receiverStallStreak = receiverStalledThisWindow
                ? Math.Min(ReceiverStallEvaluationsToDegrade, _receiverStallStreak + 1)
                : 0;
            LastDecision = _policy.Evaluate(new ConnectionPolicyInput
            {
                NetworkFeedbackAvailable = sample.NetworkFeedbackAvailable,
                PacketLossFraction = sample.PacketLossFraction,
                QueueDropRatio = dropRatio,
                FramesObserved = total,
                RttMs = sample.RttMs,
                BaselineRttMs = sample.BaselineRttMs,
                JitterMs = sample.JitterMs,
                AvailableOutgoingBitrateKbps = sample.AvailableOutgoingBitrateKbps,
                CurrentTargetBitrateKbps = Current.MaxBitrateKbps,
                ReceiverPresentationStalled =
                    _receiverStallStreak >= ReceiverStallEvaluationsToDegrade,
                ReceiverDecodeFps = sample.ReceiverDecodeFps,
                ReceiverRenderFps = sample.ReceiverRenderFps,
                ReceiverDecodeToRenderLatencyP95Ms = sample.ReceiverDecodeToRenderLatencyP95Ms,
                ReceiverCaptureToPresentLatencyP95Ms = sample.ReceiverCaptureToPresentLatencyP95Ms,
                ReceiverFrameAgeClockUncertaintyMs = sample.ReceiverFrameAgeClockUncertaintyMs,
                ReceiverCaptureToPresentTargetMs = sample.ReceiverCaptureToPresentTargetMs,
                ReceiverInputToInjectionLatencyP95Ms = sample.ReceiverInputToInjectionLatencyP95Ms,
                ReceiverInputClockUncertaintyMs = sample.ReceiverInputClockUncertaintyMs,
            });

            if (LastDecision.Action == ConnectionPolicyAction.Degrade)
            {
                _healthyStreak = 0;

                if (Current.Index < _ladder.Length - 1)
                {
                    changed = TryMoveTo(_ladder[Current.Index + 1]);
                }
            }
            else if (LastDecision.Action == ConnectionPolicyAction.Recover)
            {
                _healthyStreak++;

                if (_healthyStreak >= HealthyEvaluationsToRecover && Current.Index > 0)
                {
                    changed = TryMoveTo(_ladder[Current.Index - 1]);
                    _healthyStreak = changed is null ? HealthyEvaluationsToRecover : 0;
                }
            }
            else
            {
                // In between: hold the current level and do not count towards recovery.
                _healthyStreak = 0;
            }
        }

        if (changed is { } level)
        {
            LevelChanged?.Invoke(this, level);
        }

        return Current;
    }

    private QualityLevel? TryMoveTo(QualityLevel next)
    {
        var resolutionChanges = next.DownscaleFactor != Current.DownscaleFactor;
        var now = _time.GetUtcNow();
        if (resolutionChanges
            && _lastResolutionChange is { } last
            && now - last < MinimumResolutionDwell)
        {
            return null;
        }

        Current = next;
        if (resolutionChanges) _lastResolutionChange = now;
        return Current;
    }

    /// <summary>Forces a level, e.g. when the user picks a fixed quality in the UI.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _healthyStreak = 0;
            _receiverStallStreak = 0;
            if (Current.DownscaleFactor != _ladder[0].DownscaleFactor)
                _lastResolutionChange = _time.GetUtcNow();
            Current = _ladder[0];
        }

        LevelChanged?.Invoke(this, Current);
    }
}
