using System.Diagnostics.Metrics;
using System.Text;

namespace PeerOnQ.Signaling.Server.Diagnostics;

/// <summary>Low-cardinality counters only. Device IDs, session IDs, addresses and tokens are excluded.</summary>
public sealed class SignalingMetrics : IDisposable
{
    private readonly Meter _meter = new("PeerOnQ.Signaling", "0.5.1");
    private readonly Counter<long> _connectionsOpened;
    private readonly Counter<long> _messagesRejected;
    private readonly Counter<long> _resumeSucceeded;
    private readonly Counter<long> _resumeRejected;
    private long _opened;
    private long _rejected;
    private long _resumed;
    private long _resumeFailures;

    public SignalingMetrics()
    {
        _connectionsOpened = _meter.CreateCounter<long>("peeronq_signaling_connections_opened_total");
        _messagesRejected = _meter.CreateCounter<long>("peeronq_signaling_messages_rejected_total");
        _resumeSucceeded = _meter.CreateCounter<long>("peeronq_signaling_resumes_succeeded_total");
        _resumeRejected = _meter.CreateCounter<long>("peeronq_signaling_resumes_rejected_total");
    }

    public void ConnectionOpened()
    {
        Interlocked.Increment(ref _opened);
        _connectionsOpened.Add(1);
    }

    public void MessageRejected()
    {
        Interlocked.Increment(ref _rejected);
        _messagesRejected.Add(1);
    }

    public void ResumeSucceeded()
    {
        Interlocked.Increment(ref _resumed);
        _resumeSucceeded.Add(1);
    }

    public void ResumeRejected()
    {
        Interlocked.Increment(ref _resumeFailures);
        _resumeRejected.Add(1);
    }

    public string ExportPrometheus(int onlineDevices, int activeSessions)
    {
        var builder = new StringBuilder();
        Append(builder, "peeronq_signaling_connections_opened_total", Volatile.Read(ref _opened));
        Append(builder, "peeronq_signaling_messages_rejected_total", Volatile.Read(ref _rejected));
        Append(builder, "peeronq_signaling_resumes_succeeded_total", Volatile.Read(ref _resumed));
        Append(builder, "peeronq_signaling_resumes_rejected_total", Volatile.Read(ref _resumeFailures));
        Append(builder, "peeronq_signaling_devices_online", onlineDevices);
        Append(builder, "peeronq_signaling_sessions_active", activeSessions);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string name, long value) =>
        builder.Append(name).Append(' ').Append(value).Append('\n');

    public void Dispose() => _meter.Dispose();
}
