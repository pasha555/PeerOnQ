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

    /// <summary>Measured capture rate of the window; jitter is judged against its frame interval.</summary>
    public double SenderFrameRateFps { get; init; }
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

    /// <summary>Minimum hold at a level before a viewer-side (soft) signal may lower the rate again.</summary>
    public static readonly TimeSpan SoftDegradeDwell = TimeSpan.FromSeconds(10);

    /// <summary>Kept for 0.9.84 callers: frame age is one of the soft signals.</summary>
    public static readonly TimeSpan FrameAgeDegradeDwell = SoftDegradeDwell;

    /// <summary>An upgrade followed this quickly by a soft degrade doubles the next upgrade dwell.</summary>
    public static readonly TimeSpan FailedUpgradeWindow = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan MaximumUpgradeDwell = TimeSpan.FromMinutes(2);

    /// <summary>A level held this long without a soft degrade resets the upgrade dwell.</summary>
    public static readonly TimeSpan StableLevelResetsUpgradeDwell = TimeSpan.FromSeconds(60);

    /// <summary>Consecutive soft-signal windows required before a soft signal may degrade.</summary>
    public const int SoftEvaluationsToDegrade = 3;

    /// <summary>The reason a forced restart (new capture source) records instead of a stale one.</summary>
    public const string CaptureRestartReason = "capture_restart";

    // Jitter joined the soft signals in 0.9.87: a static desktop's sparse frames turned a clean
    // LAN's jitter into L1 -> L5 (half scale) within 3 s, three times in one session.
    private static bool IsSoftReason(string reason) =>
        reason is "viewer_frame_age" or "viewer_render_latency" or "viewer_render_backpressure"
            or "viewer_input_latency" or "high_jitter";

    /// <summary>A 4K attempt that degrades sooner than this doubles the next 4K retry dwell.</summary>
    public static readonly TimeSpan UltraHdFailureWindow = TimeSpan.FromSeconds(30);

    /// <summary>A 4K level held this long resets the 4K retry dwell to its minimum.</summary>
    public static readonly TimeSpan UltraHdStableWindow = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound of the exponential 4K retry dwell.</summary>
    public static readonly TimeSpan MaximumUltraHdRetryDwell = TimeSpan.FromMinutes(5);

    /// <summary>Frame rate of the native 4K rung, whatever the profile rate.</summary>
    public const int UltraHdMaximumFps = 30;

    private readonly QualityLevel[] _standardLadder;
    private readonly QualityLevel[] _ultraHdLadder;
    private QualityLevel[] _ladder;
    private readonly TimeProvider _time;
    private readonly IConnectionPolicyProvider _policy;
    private readonly Lock _gate = new();
    private readonly int _highMotionRungs;
    private bool _highMotionAllowed;
    private int _healthyStreak;
    private int _receiverStallStreak;
    private int _softPoorStreak;
    private DateTimeOffset? _lastResolutionChange;
    private DateTimeOffset? _lastLevelChange;
    private DateTimeOffset? _lastUpgrade;
    private DateTimeOffset? _lastSoftDegrade;
    private TimeSpan _upgradeDwell = SoftDegradeDwell;
    private bool _softHold;
    private DateTimeOffset? _ultraHdEnteredAt;
    private TimeSpan _ultraHdRetryDwell = MinimumResolutionDwell;

    public AdaptiveQualityController(
        int maxFps = 30,
        int maxBitrateKbps = 4000,
        TimeProvider? timeProvider = null,
        IConnectionPolicyProvider? policy = null,
        bool allowResolutionDownscale = true,
        int highMotionFps = 0)
    {
        _time = timeProvider ?? TimeProvider.System;
        _policy = policy ?? new ConnectionPolicyProvider();
        var top = Math.Clamp(maxFps, 1, 120);
        var highMotion = Math.Clamp(highMotionFps, 0, 120);

        // An optional high-motion rung sits above the profile's frame rate. It is earned, never
        // assumed: the session starts at the profile rate, only climbs to it after healthy
        // windows, and only while the encoder that can sustain it is actually in use.
        _highMotionRungs = highMotion > top ? 1 : 0;
        var h = _highMotionRungs;

        // Reduce frame rate first (cheapest quality loss), then resolution. A user-selected
        // high-quality session keeps its requested pixels and trades frame rate for clarity.
        // Half scale is the floor: a quarter-scale desktop (480x270 from 1080p) makes text
        // unreadable, which looks broken rather than degraded; the last rung lowers rate instead.
        var firstResolutionDownscale = allowResolutionDownscale ? 2 : 1;
        var finalResolutionDownscale = firstResolutionDownscale;
        QualityLevel[] standard =
        [
            new QualityLevel(h, top, 1, Math.Max(250, maxBitrateKbps)),
            new QualityLevel(h + 1, Math.Max(1, top * 2 / 3), 1, Math.Max(250, maxBitrateKbps * 3 / 4)),
            new QualityLevel(h + 2, Math.Max(1, top / 2), 1, Math.Max(250, maxBitrateKbps / 2)),
            new QualityLevel(h + 3, Math.Max(1, top / 2), firstResolutionDownscale, Math.Max(250, maxBitrateKbps / 3)),
            new QualityLevel(h + 4, Math.Max(1, top / 3), finalResolutionDownscale, Math.Max(250, maxBitrateKbps / 5)),
        ];
        _standardLadder = h == 0
            ? standard
            : [new QualityLevel(0, highMotion, 1, HighMotionBitrateKbps(maxBitrateKbps)), .. standard];

        // A 4K source gives up pixels before motion: 1080p at the full frame rate stays fluid,
        // whereas 4K at a third of the frame rate looks frozen. Rung 1 is the standard tier.
        // 4K itself stays at 30 fps (the hardware encoders' sustainable rate); below it the
        // standard tier runs at the profile rate.
        _ultraHdLadder =
        [
            new QualityLevel(0, Math.Min(top, UltraHdMaximumFps), 1, UltraHdBitrateKbps(maxBitrateKbps)),
            new QualityLevel(1, top, 2, Math.Max(250, maxBitrateKbps)),
            new QualityLevel(2, Math.Max(1, top * 2 / 3), 2, Math.Max(250, maxBitrateKbps * 3 / 4)),
            new QualityLevel(3, Math.Max(1, top / 2), 2, Math.Max(250, maxBitrateKbps / 2)),
            new QualityLevel(4, Math.Max(1, top / 3), 2, Math.Max(250, maxBitrateKbps / 3)),
        ];

        _ladder = _standardLadder;
        Current = _ladder[h];
    }

    /// <summary>4K tier bitrate ceiling derived from the profile's standard-tier ceiling.</summary>
    public static int UltraHdBitrateKbps(int standardBitrateKbps) =>
        Math.Clamp(standardBitrateKbps * 5 / 2, Math.Max(250, standardBitrateKbps), 50_000);

    /// <summary>High-motion rung bitrate: twice the frames need more bits, not twice the bits.</summary>
    public static int HighMotionBitrateKbps(int standardBitrateKbps) =>
        Math.Clamp(standardBitrateKbps * 3 / 2, Math.Max(250, standardBitrateKbps), 50_000);

    /// <summary>True when the standard ladder carries a high-motion rung above the profile rate.</summary>
    public bool HasHighMotionRung => _highMotionRungs != 0;

    /// <summary>Whether the high-motion rung may be used now; see <see cref="SetHighMotionAllowed"/>.</summary>
    public bool IsHighMotionAllowed
    {
        get { lock (_gate) return _highMotionAllowed; }
    }

    /// <summary>
    /// Enables the high-motion rung only while it can be sustained (hardware encoding of a
    /// standard-tier source). Withdrawing it leaves that rung at once for the profile rate.
    /// </summary>
    public void SetHighMotionAllowed(bool allowed)
    {
        if (_highMotionRungs == 0) return;
        QualityLevel? changed = null;
        lock (_gate)
        {
            if (_highMotionAllowed == allowed) return;
            _highMotionAllowed = allowed;
            if (!allowed && ReferenceEquals(_ladder, _standardLadder) && _current.Index < _highMotionRungs)
            {
                Current = _ladder[_highMotionRungs];
                _healthyStreak = 0;
                changed = _current;
            }
        }

        if (changed is { } level) LevelChanged?.Invoke(this, level);
    }

    /// <summary>The best rung recovery may reach on the active ladder right now.</summary>
    private int TopIndex =>
        ReferenceEquals(_ladder, _standardLadder) && !_highMotionAllowed ? _highMotionRungs : 0;

    /// <summary>The rung a fresh or reset session starts from on the active ladder.</summary>
    private int BaseIndex => ReferenceEquals(_ladder, _standardLadder) ? _highMotionRungs : 0;

    private QualityLevel _current;

    /// <summary>The applied rung; read under the controller lock so a 16-byte level never tears.</summary>
    public QualityLevel Current
    {
        get { lock (_gate) return _current; }
        private set => _current = value;
    }

    /// <summary>True while the resolution-first 4K ladder is active.</summary>
    public bool IsUltraHdLadder
    {
        get { lock (_gate) return ReferenceEquals(_ladder, _ultraHdLadder); }
    }

    /// <summary>
    /// Selects the resolution-first ladder for a 4K-class source and the standard ladder for any
    /// other source, keeping the equivalent quality: 4K rung n+1 is standard rung n.
    /// </summary>
    public void SetUltraHdSource(bool ultraHd)
    {
        QualityLevel changed;
        lock (_gate)
        {
            var target = ultraHd ? _ultraHdLadder : _standardLadder;
            if (ReferenceEquals(_ladder, target)) return;
            // The standard base rung (profile rate, full scale) maps to 4K rung 0; a high-motion
            // rung shifts every standard index by one. 4K sources never use the high-motion rung.
            var h = _highMotionRungs;
            var index = ultraHd
                ? (_current.Index <= h ? 0 : Math.Min(_current.Index - h + 1, target.Length - 1))
                : Math.Max(h, _current.Index - 1 + h);
            var previous = _current;
            _ladder = target;
            Current = target[index];
            _healthyStreak = 0;
            if (ultraHd && _current.Index == 0) _ultraHdEnteredAt = _time.GetUtcNow();
            if (previous.DownscaleFactor != _current.DownscaleFactor) _lastResolutionChange = _time.GetUtcNow();
            changed = _current;
        }

        LevelChanged?.Invoke(this, changed);
    }

    /// <summary>The sample behind <see cref="LastDecision"/>, for decision logs.</summary>
    public QualitySample? LastSample { get; private set; }

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
            LastSample = sample;
            LastDecision = _policy.Evaluate(new ConnectionPolicyInput
            {
                NetworkFeedbackAvailable = sample.NetworkFeedbackAvailable,
                PacketLossFraction = sample.PacketLossFraction,
                QueueDropRatio = dropRatio,
                FramesObserved = total,
                RttMs = sample.RttMs,
                BaselineRttMs = sample.BaselineRttMs,
                JitterMs = sample.JitterMs,
                SenderFrameRateFps = sample.SenderFrameRateFps,
                AvailableOutgoingBitrateKbps = sample.AvailableOutgoingBitrateKbps,
                CurrentTargetBitrateKbps = _current.MaxBitrateKbps,
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
                var soft = IsSoftReason(LastDecision.ReasonCode);
                _softPoorStreak = soft ? Math.Min(SoftEvaluationsToDegrade, _softPoorStreak + 1) : 0;

                if (_current.Index < _ladder.Length - 1)
                {
                    var next = _ladder[_current.Index + 1];
                    // Viewer-side signals (frame age, render latency, render backpressure) are
                    // soft: encode time, frame spacing, clock offset and a slow presenter inflate
                    // them, and the windows still hold samples from the previous level right after
                    // a change. They must persist for SoftEvaluationsToDegrade windows, lower one
                    // level per SoftDegradeDwell and only the frame rate, never the scale; a
                    // render-bound viewer needs fewer frames, not fewer pixels. Input latency and
                    // jitter follow the same rules. Loss, RTT, bandwidth and stalls still degrade
                    // at once. On 0.9.84 a clean LAN made 168 changes and sat at L5 66% of the time.
                    var now = _time.GetUtcNow();
                    if (!soft)
                    {
                        _softHold = false;
                        changed = TryMoveTo(next);
                    }
                    else if (_softPoorStreak >= SoftEvaluationsToDegrade
                             && (_lastLevelChange is not { } last || now - last >= SoftDegradeDwell)
                             && next.DownscaleFactor == _current.DownscaleFactor)
                    {
                        changed = TryMoveTo(next);
                        if (changed is not null)
                        {
                            // An upgrade that the viewer could not hold makes the next try wait longer.
                            _upgradeDwell = _lastUpgrade is { } upgraded && now - upgraded <= FailedUpgradeWindow
                                ? TimeSpan.FromTicks(Math.Min(_upgradeDwell.Ticks * 2, MaximumUpgradeDwell.Ticks))
                                : _upgradeDwell;
                            _softHold = true;
                            _lastSoftDegrade = now;
                        }
                    }
                }
            }
            else if (LastDecision.Action == ConnectionPolicyAction.Recover)
            {
                _softPoorStreak = 0;
                // Nothing encoded is no evidence of headroom (a stalled capture once climbed to L1).
                if (sample.FramesEncoded > 0) _healthyStreak++;

                var now = _time.GetUtcNow();
                var held = _lastLevelChange is not { } last ? TimeSpan.MaxValue : now - last;
                // Only an upgraded level that held without a soft degrade proves the viewer copes again.
                if (_lastUpgrade is { } upgradedAt
                    && (_lastSoftDegrade is not { } softAt || softAt < upgradedAt)
                    && now - upgradedAt >= StableLevelResetsUpgradeDwell)
                    _upgradeDwell = SoftDegradeDwell;
                if (_healthyStreak >= HealthyEvaluationsToRecover && _current.Index > TopIndex
                    && (!_softHold || held >= _upgradeDwell))
                {
                    changed = TryMoveTo(_ladder[_current.Index - 1]);
                    _healthyStreak = changed is null ? HealthyEvaluationsToRecover : 0;
                    if (changed is not null) _lastUpgrade = now;
                }
            }
            else
            {
                // In between: hold the current level and do not count towards recovery.
                _healthyStreak = 0;
                _softPoorStreak = 0;
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
        var resolutionChanges = next.DownscaleFactor != _current.DownscaleFactor;
        var now = _time.GetUtcNow();
        if (ReferenceEquals(_ladder, _ultraHdLadder))
        {
            if (next.Index == 0 && _current.Index != 0)
            {
                // Returning to 4K re-initializes the encoder with a large key frame. Retry only
                // after a dwell that doubles whenever the previous 4K attempt could not hold.
                if (_lastResolutionChange is { } last && now - last < _ultraHdRetryDwell)
                    return null;
                _ultraHdEnteredAt = now;
            }
            else if (_current.Index == 0 && next.Index != 0)
            {
                // Leaving 4K is never delayed: an overloaded 4K stream is what looks frozen.
                if (_ultraHdEnteredAt is { } entered)
                {
                    var held = now - entered;
                    if (held < UltraHdFailureWindow)
                    {
                        var doubled = TimeSpan.FromTicks(Math.Min(
                            _ultraHdRetryDwell.Ticks * 2, MaximumUltraHdRetryDwell.Ticks));
                        _ultraHdRetryDwell = doubled;
                    }
                    else if (held >= UltraHdStableWindow)
                    {
                        _ultraHdRetryDwell = MinimumResolutionDwell;
                    }
                }
                _ultraHdEnteredAt = null;
            }
        }
        else if (resolutionChanges
                 && _lastResolutionChange is { } last
                 && now - last < MinimumResolutionDwell)
        {
            return null;
        }

        Current = next;
        _lastLevelChange = now;
        if (resolutionChanges) _lastResolutionChange = now;
        return _current;
    }

    /// <summary>Current wait before a 4K retry after the stream fell back to the standard tier.</summary>
    public TimeSpan UltraHdRetryDwell
    {
        get { lock (_gate) return _ultraHdRetryDwell; }
    }

    /// <summary>Forces a level, e.g. when the user picks a fixed quality in the UI.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _healthyStreak = 0;
            _receiverStallStreak = 0;
            _softPoorStreak = 0;
            // The previous decision described the old source; do not log it against the new one.
            LastDecision = new ConnectionPolicyDecision(
                ConnectionHealth.Unknown, ConnectionPolicyAction.Hold, CaptureRestartReason);
            var start = _ladder[BaseIndex];
            _lastLevelChange = _time.GetUtcNow();
            _softHold = false;
            _upgradeDwell = SoftDegradeDwell;
            if (_current.DownscaleFactor != start.DownscaleFactor)
                _lastResolutionChange = _time.GetUtcNow();
            Current = start;
            if (ReferenceEquals(_ladder, _ultraHdLadder)) _ultraHdEnteredAt = _time.GetUtcNow();
        }

        LevelChanged?.Invoke(this, Current);
    }
}
