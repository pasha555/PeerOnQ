using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Application.Collaboration;

internal readonly record struct NativeBulkCapacitySnapshot(
    int BudgetKbps,
    double GoodputKbps,
    long FeedbackSamples);

/// <summary>
/// Learns only from authenticated bytes that the peer confirms after its disk write completes on
/// an isolated bulk path (native QUIC or dedicated WebRTC SCTP). The probe is bounded, grows
/// gradually, and never overrides a degraded media/input policy.
/// </summary>
internal sealed class NativeBulkCapacityEstimator(TimeProvider? timeProvider = null)
{
    private const int MaximumManagedBulkKbps = 1_000_000;
    private const int MaximumUnverifiedHealthBulkKbps = 150_000;
    private const int MinimumSampleBytes = 256 * 1024;
    private const double GrowthFactor = 1.25;
    private static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MinimumBackoffInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumFeedbackAge = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, OutboundTransferState> _outbound = [];
    private int _probeKbps;
    private double _goodputKbps;
    private long _feedbackSamples;
    private long _lastFeedbackTimestamp;
    private long _lastBackoffTimestamp;

    public NativeBulkCapacitySnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new NativeBulkCapacitySnapshot(_probeKbps, _goodputKbps, _feedbackSamples);
        }
    }

    public void RecordChunkScheduled(Guid transferId, int payloadBytes, int baselineKbps)
    {
        if (transferId == Guid.Empty) throw new ArgumentException("A transfer id is required.", nameof(transferId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baselineKbps);

        lock (_gate)
        {
            if (!_outbound.TryGetValue(transferId, out var state))
            {
                state = new OutboundTransferState { LastReceiptTimestamp = _time.GetTimestamp() };
                _outbound.Add(transferId, state);
            }

            state.ScheduledBytes = state.ScheduledBytes > long.MaxValue - payloadBytes
                ? long.MaxValue
                : state.ScheduledBytes + payloadBytes;
            _probeKbps = Math.Max(_probeKbps, baselineKbps);
        }
    }

    public void RecordChunkFailed(Guid transferId, int payloadBytes)
    {
        if (payloadBytes <= 0) return;
        lock (_gate)
        {
            if (_outbound.TryGetValue(transferId, out var state))
                state.ScheduledBytes = Math.Max(state.DeliveredBytes, state.ScheduledBytes - payloadBytes);
        }
    }

    public bool ObserveReceipt(
        Guid transferId,
        long deliveredBytes,
        int baselineKbps,
        MediaStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        if (transferId == Guid.Empty || deliveredBytes <= 0 || baselineKbps <= 0) return false;

        lock (_gate)
        {
            if (!_outbound.TryGetValue(transferId, out var state)
                || deliveredBytes <= state.DeliveredBytes
                || deliveredBytes > state.ScheduledBytes)
            {
                return false;
            }

            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(state.LastReceiptTimestamp, now);
            var delta = deliveredBytes - state.DeliveredBytes;
            if (delta < MinimumSampleBytes || elapsed < MinimumSampleInterval) return false;

            state.DeliveredBytes = deliveredBytes;
            state.LastReceiptTimestamp = now;
            var sampleKbps = delta * 8d / elapsed.TotalSeconds / 1000d;
            if (!double.IsFinite(sampleKbps) || sampleKbps <= 0) return false;

            _goodputKbps = _feedbackSamples == 0
                ? sampleKbps
                : (_goodputKbps * 0.75) + (sampleKbps * 0.25);
            _feedbackSamples++;
            _lastFeedbackTimestamp = now;
            _probeKbps = Math.Max(_probeKbps, baselineKbps);

            if (IsDegraded(statistics))
            {
                BackOff(baselineKbps, now);
                return true;
            }

            var cap = CapacityCap(statistics);
            var current = Math.Clamp(_probeKbps, baselineKbps, cap);
            if (sampleKbps >= current * 0.90 || _goodputKbps >= current * 0.90)
            {
                var grown = Math.Max(current + 256, (int)Math.Ceiling(current * GrowthFactor));
                _probeKbps = Math.Min(cap, grown);
            }
            else if (_goodputKbps < current * 0.75)
            {
                _probeKbps = Math.Clamp(
                    (int)Math.Ceiling(_goodputKbps * 1.05),
                    baselineKbps,
                    cap);
            }

            return true;
        }
    }

    public TransferAllocationPolicy Apply(
        TransferAllocationPolicy baseline,
        MediaStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        if (baseline.MaximumBulkKbps <= 0) return baseline;

        lock (_gate)
        {
            if (IsDegraded(statistics))
            {
                BackOff(baseline.MaximumBulkKbps, _time.GetTimestamp());
                return baseline;
            }

            if (_feedbackSamples == 0) return baseline;
            var now = _time.GetTimestamp();
            if (_time.GetElapsedTime(_lastFeedbackTimestamp, now) > MaximumFeedbackAge)
            {
                BackOff(baseline.MaximumBulkKbps, now);
                return baseline;
            }
            var budget = Math.Clamp(
                Math.Max(baseline.MaximumBulkKbps, _probeKbps),
                baseline.MaximumBulkKbps,
                CapacityCap(statistics));
            return baseline with { MaximumBulkKbps = budget };
        }
    }

    public void CompleteTransfer(Guid transferId)
    {
        lock (_gate) _outbound.Remove(transferId);
    }

    private void BackOff(int baselineKbps, long now)
    {
        if (_probeKbps <= baselineKbps) return;
        if (_lastBackoffTimestamp != 0
            && _time.GetElapsedTime(_lastBackoffTimestamp, now) < MinimumBackoffInterval)
        {
            return;
        }

        _probeKbps = Math.Max(baselineKbps, _probeKbps / 2);
        _lastBackoffTimestamp = now;
    }

    private static int CapacityCap(MediaStatistics statistics) =>
        statistics.ConnectionHealth is ConnectionHealth.Good or ConnectionHealth.Excellent
            ? MaximumManagedBulkKbps
            : MaximumUnverifiedHealthBulkKbps;

    private static bool IsDegraded(MediaStatistics statistics)
    {
        if (!Enum.IsDefined(statistics.ConnectionHealth)
            || !double.IsFinite(statistics.CurrentBitrateKbps)
            || statistics.CurrentBitrateKbps < 0
            || !double.IsFinite(statistics.AvailableOutgoingBitrateKbps)
            || statistics.AvailableOutgoingBitrateKbps < 0
            || statistics.TargetBitrateKbps < 0
            || !double.IsFinite(statistics.PacketLossPercent)
            || statistics.PacketLossPercent < 0
            || !double.IsFinite(statistics.RttMs)
            || statistics.RttMs < 0
            || !double.IsFinite(statistics.JitterMs)
            || statistics.JitterMs < 0
            || !double.IsFinite(statistics.InputToInjectionLatencyP95Ms)
            || statistics.InputToInjectionLatencyP95Ms < 0
            || !double.IsFinite(statistics.InputClockUncertaintyMs)
            || statistics.InputClockUncertaintyMs < 0)
        {
            return true;
        }

        var inputPressure = ConnectionPolicyProvider.HasReliableInputPressure(
            statistics.InputToInjectionLatencyP95Ms,
            statistics.InputClockUncertaintyMs);
        return inputPressure
               || statistics.ConnectionHealth is ConnectionHealth.Poor or ConnectionHealth.Fair
               || statistics.PacketLossPercent >= ConnectionPolicyProvider.DegradeLossFraction * 100
               || statistics.RttMs >= ConnectionPolicyProvider.DegradeRttMs
               || statistics.JitterMs >= ConnectionPolicyProvider.DegradeJitterMs;
    }

    private sealed class OutboundTransferState
    {
        public long ScheduledBytes { get; set; }
        public long DeliveredBytes { get; set; }
        public long LastReceiptTimestamp { get; set; }
    }
}
