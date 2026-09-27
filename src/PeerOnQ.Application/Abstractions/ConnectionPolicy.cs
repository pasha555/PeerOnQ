namespace PeerOnQ.Application.Abstractions;

public enum ConnectionHealth
{
    Unknown = 0,
    Excellent = 1,
    Good = 2,
    Fair = 3,
    Poor = 4,
}

public enum ConnectionPolicyAction
{
    Hold = 0,
    Degrade = 1,
    Recover = 2,
}

public enum TransferPriorityMode
{
    RemoteControlPriority = 0,
    Balanced = 1,
    FileTransferPriority = 2,
}

public readonly record struct TransferAllocationPolicy(
    ulong MaximumBulkBufferedBytes,
    int MaximumBulkKbps = 0,
    int InteractiveReserveKbps = 0,
    int MediaReserveKbps = 0);

/// <summary>
/// Address-free measurements used by the connection policy. Authentication, authorization,
/// consent, cryptography and relay trust are deliberately outside this optimization boundary.
/// </summary>
public readonly record struct ConnectionPolicyInput()
{
    public required double PacketLossFraction { get; init; }
    public required double QueueDropRatio { get; init; }
    public required long FramesObserved { get; init; }
    public bool NetworkFeedbackAvailable { get; init; } = true;
    public double RttMs { get; init; }
    public double BaselineRttMs { get; init; }
    public double JitterMs { get; init; }
    public double AvailableOutgoingBitrateKbps { get; init; }
    public double CurrentTargetBitrateKbps { get; init; }
    public bool ReceiverPresentationStalled { get; init; }
    public double ReceiverDecodeFps { get; init; }
    public double ReceiverRenderFps { get; init; }
    public double ReceiverDecodeToRenderLatencyP95Ms { get; init; }
    public double ReceiverCaptureToPresentLatencyP95Ms { get; init; }
    public double ReceiverFrameAgeClockUncertaintyMs { get; init; }
    public double ReceiverCaptureToPresentTargetMs { get; init; }
    public double ReceiverInputToInjectionLatencyP95Ms { get; init; }
    public double ReceiverInputClockUncertaintyMs { get; init; }
}

public sealed record ConnectionPolicyDecision(
    ConnectionHealth Health,
    ConnectionPolicyAction Action,
    string ReasonCode);

public interface IConnectionPolicyProvider
{
    ConnectionPolicyDecision Evaluate(ConnectionPolicyInput input);
}

/// <summary>
/// Extension point for an independently versioned and validated model. The default build has no
/// implementation and never requires an AI provider; callers always use the deterministic policy.
/// </summary>
public interface IMachineLearningConnectionPolicyProvider : IConnectionPolicyProvider
{
    string ModelVersion { get; }
}

/// <summary>Production deterministic connection policy used by the existing media pipeline.</summary>
public sealed class ConnectionPolicyProvider : IConnectionPolicyProvider
{
    private const int MaximumManagedBulkKbps = 1_000_000;
    private const int MaximumProvisionalBulkKbps = 150_000;
    private const int InputPressureInteractiveReserveKbps = 1_024;
    public const double TargetInputToInjectionP95Ms = 35;
    public const double MaximumAdaptiveInputClockUncertaintyMs = 10;
    public const double DegradeLossFraction = 0.05;
    public const double RecoverLossFraction = 0.01;
    public const double DegradeDropRatio = 0.20;
    public const double DegradeRttMs = 350;
    public const double DegradeJitterMs = 80;
    public const double MaximumAdaptiveFrameAgeClockUncertaintyMs = 10;
    public const double DegradeDecodeToRenderLatencyP95Ms = 50;
    public const double MinimumPresentationFpsForBackpressure = 5;
    public const double MinimumRenderToDecodeRatio = 0.75;
    public const double TargetCaptureToPresentP95Ms = 75;
    public const double TargetFourKCaptureToPresentP95Ms = 100;

    public static double GetTargetCaptureToPresentP95Ms(CaptureResolution resolution) =>
        resolution is CaptureResolution.P2160 or CaptureResolution.Native
            ? TargetFourKCaptureToPresentP95Ms
            : TargetCaptureToPresentP95Ms;

    public static bool HasReliableInputPressure(double latencyP95Ms, double clockUncertaintyMs) =>
        IsFiniteNonNegative(latencyP95Ms)
        && IsFiniteNonNegative(clockUncertaintyMs)
        && latencyP95Ms > TargetInputToInjectionP95Ms
        && clockUncertaintyMs <= MaximumAdaptiveInputClockUncertaintyMs;

    public static TransferAllocationPolicy GetTransferAllocation(TransferPriorityMode mode) => mode switch
    {
        // Interactive/security messages still preempt bulk traffic in every mode. These bounded
        // windows only determine how much spare data-channel capacity file chunks may occupy.
        // Keeping a few MiB in flight is sufficient for high-throughput links while avoiding the
        // SCTP sender's congestion-collapse behaviour when a very large application queue builds.
        TransferPriorityMode.RemoteControlPriority => new(256UL * 1024),
        TransferPriorityMode.Balanced => new(512UL * 1024),
        TransferPriorityMode.FileTransferPriority => new(2UL * 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>
    /// Reserves capacity for authenticated interactive traffic and the active adaptive video target.
    /// A zero bulk rate means there is no live video to reserve. With live media but no reliable
    /// bandwidth estimate, a bounded provisional rate prevents an unlimited file burst. Once media
    /// measurements exist, file traffic is paced to the measured spare capacity and is reduced
    /// before video or input when conditions deteriorate.
    /// </summary>
    public static TransferAllocationPolicy GetTransferAllocation(
        TransferPriorityMode mode,
        MediaStatistics statistics,
        bool hasInteractiveTraffic = false)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        var baseline = GetTransferAllocation(mode);
        var interactiveReserve = mode switch
        {
            TransferPriorityMode.RemoteControlPriority => 512,
            TransferPriorityMode.Balanced => 384,
            TransferPriorityMode.FileTransferPriority => 256,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        if (!double.IsFinite(statistics.CurrentBitrateKbps)
            || statistics.CurrentBitrateKbps < 0
            || !double.IsFinite(statistics.AvailableOutgoingBitrateKbps)
            || statistics.AvailableOutgoingBitrateKbps < 0
            || statistics.TargetBitrateKbps < 0
            || !double.IsFinite(statistics.InputToInjectionLatencyP95Ms)
            || statistics.InputToInjectionLatencyP95Ms < 0
            || !double.IsFinite(statistics.InputClockUncertaintyMs)
            || statistics.InputClockUncertaintyMs < 0)
        {
            return new TransferAllocationPolicy(
                64UL * 1024,
                PoorBulkKbps(mode),
                interactiveReserve,
                0);
        }

        var reliableInputPressure = HasReliableInputPressure(
            statistics.InputToInjectionLatencyP95Ms,
            statistics.InputClockUncertaintyMs);
        var mediaDemand = Math.Max(statistics.CurrentBitrateKbps, statistics.TargetBitrateKbps);
        if (mediaDemand <= 0)
        {
            if (reliableInputPressure)
            {
                return new TransferAllocationPolicy(
                    64UL * 1024,
                    PoorBulkKbps(mode),
                    Math.Max(interactiveReserve, InputPressureInteractiveReserveKbps),
                    0);
            }

            if (hasInteractiveTraffic)
            {
                // A control-capable session can begin transferring before the first video or
                // bandwidth sample exists. Do not let that startup window enqueue an unlimited
                // file burst on the association shared with input acknowledgements.
                return baseline with
                {
                    MaximumBulkKbps = ProvisionalBulkKbps(mode),
                    InteractiveReserveKbps = interactiveReserve,
                };
            }

            // A file-only session has no live video capacity to reserve. Retain bounded buffering
            // and allow the transport to consume the link normally.
            return baseline with { InteractiveReserveKbps = interactiveReserve };
        }

        var mediaHeadroom = mode switch
        {
            TransferPriorityMode.RemoteControlPriority => 1.15,
            TransferPriorityMode.Balanced => 1.10,
            TransferPriorityMode.FileTransferPriority => 1.05,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var mediaReserve = ClampKbps(mediaDemand * mediaHeadroom);
        if (reliableInputPressure)
        {
            // The authenticated input path is already outside the file lane. If injection p95 is
            // nevertheless above budget, drain bulk queues and reduce file rate first instead of
            // sacrificing control responsiveness or video freshness.
            return new TransferAllocationPolicy(
                64UL * 1024,
                PoorBulkKbps(mode),
                Math.Max(interactiveReserve, InputPressureInteractiveReserveKbps),
                mediaReserve);
        }

        var minimumBulk = MinimumBulkKbps(mode);
        var available = ClampKbps(statistics.AvailableOutgoingBitrateKbps);
        var health = Enum.IsDefined(statistics.ConnectionHealth)
            ? statistics.ConnectionHealth
            : ConnectionHealth.Poor;
        // Expiring a bad RTCP report is not evidence of spare capacity. Keep file traffic at
        // the conservative tier until fresh feedback permits media recovery.
        if (statistics.QualityChangeReason == "awaiting_network_feedback")
            health = ConnectionHealth.Poor;
        if (available <= 0 && health is ConnectionHealth.Excellent or ConnectionHealth.Good or ConnectionHealth.Unknown)
        {
            // SIPSorcery does not expose a transport-wide bandwidth estimate. A quiet 4K desktop
            // therefore has no trustworthy spare-capacity number even on a fast LAN. A finite
            // provisional ceiling prevents relay traffic from filling the NIC/router queue while
            // still allowing the file-priority mode to reach the one-GiB-per-minute target on a
            // capable link. Loss, RTT, jitter or an available-rate sample replace this ceiling.
            return new TransferAllocationPolicy(
                baseline.MaximumBulkBufferedBytes,
                ProvisionalBulkKbps(mode),
                interactiveReserve,
                mediaReserve);
        }
        var measuredSpare = Math.Max(0, available - mediaReserve - interactiveReserve);
        var healthyBulk = available > 0
            ? Math.Max(minimumBulk, measuredSpare)
            : UnknownBulkKbps(mode);
        var maximumBulk = health switch
        {
            ConnectionHealth.Poor => Math.Min(healthyBulk, PoorBulkKbps(mode)),
            ConnectionHealth.Fair => Math.Min(healthyBulk, FairBulkKbps(mode)),
            ConnectionHealth.Unknown => Math.Min(healthyBulk, UnknownBulkKbps(mode)),
            _ => healthyBulk,
        };
        var maximumBuffered = health switch
        {
            ConnectionHealth.Poor => 64UL * 1024,
            ConnectionHealth.Fair => Math.Min(baseline.MaximumBulkBufferedBytes, 128UL * 1024),
            ConnectionHealth.Unknown => Math.Min(baseline.MaximumBulkBufferedBytes, 256UL * 1024),
            _ => baseline.MaximumBulkBufferedBytes,
        };

        return new TransferAllocationPolicy(
            maximumBuffered,
            Math.Clamp(maximumBulk, 1, MaximumManagedBulkKbps),
            interactiveReserve,
            mediaReserve);
    }

    public ConnectionPolicyDecision Evaluate(ConnectionPolicyInput input)
    {
        if (!IsFiniteNonNegative(input.PacketLossFraction)
            || !IsFiniteNonNegative(input.QueueDropRatio)
            || !IsFiniteNonNegative(input.RttMs)
            || !IsFiniteNonNegative(input.BaselineRttMs)
            || !IsFiniteNonNegative(input.JitterMs)
            || !IsFiniteNonNegative(input.AvailableOutgoingBitrateKbps)
            || !IsFiniteNonNegative(input.CurrentTargetBitrateKbps)
            || !IsFiniteNonNegative(input.ReceiverDecodeFps)
            || !IsFiniteNonNegative(input.ReceiverRenderFps)
            || !IsFiniteNonNegative(input.ReceiverDecodeToRenderLatencyP95Ms)
            || !IsFiniteNonNegative(input.ReceiverCaptureToPresentLatencyP95Ms)
            || !IsFiniteNonNegative(input.ReceiverFrameAgeClockUncertaintyMs)
            || !IsFiniteNonNegative(input.ReceiverCaptureToPresentTargetMs)
            || !IsFiniteNonNegative(input.ReceiverInputToInjectionLatencyP95Ms)
            || !IsFiniteNonNegative(input.ReceiverInputClockUncertaintyMs)
            || input.FramesObserved < 0)
        {
            return new ConnectionPolicyDecision(
                ConnectionHealth.Poor,
                ConnectionPolicyAction.Degrade,
                "invalid_telemetry");
        }

        var bitrateConstrained = input.AvailableOutgoingBitrateKbps > 0
                                 && input.CurrentTargetBitrateKbps > 0
                                 && input.AvailableOutgoingBitrateKbps
                                 < input.CurrentTargetBitrateKbps * 0.8;

        // A fresh path-minimum RTT estimates one-way transit without treating a growing queue
        // as unavoidable delay. Bound the allowance; bulk still reacts to full input latency.
        var estimatedTransitMs = Math.Min(input.BaselineRttMs / 2, 75);
        if (HasReliableInputPressure(
                Math.Max(0, input.ReceiverInputToInjectionLatencyP95Ms - estimatedTransitMs),
                input.ReceiverInputClockUncertaintyMs))
        {
            return Poor("viewer_input_latency");
        }
        if (input.ReceiverPresentationStalled)
            return Poor("viewer_video_stall");
        if (input.PacketLossFraction >= DegradeLossFraction)
            return Poor("packet_loss");
        if (input.QueueDropRatio >= DegradeDropRatio)
            return Poor("encoder_backpressure");
        if (input.RttMs >= DegradeRttMs)
            return Poor("high_rtt");
        if (input.JitterMs >= DegradeJitterMs)
            return Poor("high_jitter");
        if (bitrateConstrained)
            return Poor("insufficient_bandwidth");

        var frameAgeTarget = input.ReceiverCaptureToPresentTargetMs > 0
            ? input.ReceiverCaptureToPresentTargetMs
            : TargetCaptureToPresentP95Ms;
        var reliableFrameAgePressure = input.ReceiverCaptureToPresentLatencyP95Ms
                                       > frameAgeTarget + estimatedTransitMs
                                       && input.ReceiverFrameAgeClockUncertaintyMs
                                       <= MaximumAdaptiveFrameAgeClockUncertaintyMs;
        if (reliableFrameAgePressure)
            return Poor("viewer_frame_age");
        if (input.ReceiverDecodeToRenderLatencyP95Ms > DegradeDecodeToRenderLatencyP95Ms)
            return Poor("viewer_render_latency");

        var renderBackpressured = input.ReceiverDecodeFps >= MinimumPresentationFpsForBackpressure
                                  && input.ReceiverRenderFps
                                  < input.ReceiverDecodeFps * MinimumRenderToDecodeRatio;
        if (renderBackpressured)
            return Poor("viewer_render_backpressure");

        if (!input.NetworkFeedbackAvailable)
        {
            return new ConnectionPolicyDecision(
                ConnectionHealth.Unknown,
                ConnectionPolicyAction.Hold,
                "awaiting_network_feedback");
        }

        var hasMeasurements = input.FramesObserved > 0
                              || input.RttMs > 0
                              || input.JitterMs > 0
                              || input.PacketLossFraction > 0
                              || input.AvailableOutgoingBitrateKbps > 0
                              || input.ReceiverDecodeFps > 0
                              || input.ReceiverRenderFps > 0
                              || input.ReceiverDecodeToRenderLatencyP95Ms > 0
                              || input.ReceiverCaptureToPresentLatencyP95Ms > 0
                              || input.ReceiverInputToInjectionLatencyP95Ms > 0;
        if (!hasMeasurements)
        {
            return new ConnectionPolicyDecision(
                ConnectionHealth.Unknown,
                ConnectionPolicyAction.Hold,
                "awaiting_measurements");
        }

        var recoverable = input.PacketLossFraction <= RecoverLossFraction
                          && input.QueueDropRatio < DegradeDropRatio / 2
                          && (input.RttMs <= 0 || input.RttMs < DegradeRttMs / 2)
                          && (input.JitterMs <= 0 || input.JitterMs < DegradeJitterMs / 2)
                          && !bitrateConstrained;

        if (!recoverable)
        {
            return new ConnectionPolicyDecision(
                ConnectionHealth.Fair,
                ConnectionPolicyAction.Hold,
                "marginal_conditions");
        }

        var excellent = input.PacketLossFraction <= 0.005
                        && input.QueueDropRatio < 0.02
                        && (input.RttMs <= 0 || input.RttMs <= 80)
                        && (input.JitterMs <= 0 || input.JitterMs <= 20);
        return new ConnectionPolicyDecision(
            excellent ? ConnectionHealth.Excellent : ConnectionHealth.Good,
            ConnectionPolicyAction.Recover,
            excellent ? "stable_headroom" : "stable");
    }

    private static ConnectionPolicyDecision Poor(string reasonCode) =>
        new(ConnectionHealth.Poor, ConnectionPolicyAction.Degrade, reasonCode);

    private static int MinimumBulkKbps(TransferPriorityMode mode) => mode switch
    {
        TransferPriorityMode.RemoteControlPriority => 256,
        TransferPriorityMode.Balanced => 512,
        TransferPriorityMode.FileTransferPriority => 1_024,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static int PoorBulkKbps(TransferPriorityMode mode) => mode switch
    {
        TransferPriorityMode.RemoteControlPriority => 64,
        TransferPriorityMode.Balanced => 128,
        TransferPriorityMode.FileTransferPriority => 256,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static int FairBulkKbps(TransferPriorityMode mode) => mode switch
    {
        TransferPriorityMode.RemoteControlPriority => 256,
        TransferPriorityMode.Balanced => 512,
        TransferPriorityMode.FileTransferPriority => 1_024,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static int UnknownBulkKbps(TransferPriorityMode mode) => mode switch
    {
        TransferPriorityMode.RemoteControlPriority => 256,
        TransferPriorityMode.Balanced => 512,
        TransferPriorityMode.FileTransferPriority => 1_024,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static int ProvisionalBulkKbps(TransferPriorityMode mode) => mode switch
    {
        TransferPriorityMode.RemoteControlPriority => 50_000,
        TransferPriorityMode.Balanced => 100_000,
        TransferPriorityMode.FileTransferPriority => MaximumProvisionalBulkKbps,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static int ClampKbps(double value) =>
        (int)Math.Clamp(Math.Ceiling(value), 0, MaximumManagedBulkKbps);

    private static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;
}
