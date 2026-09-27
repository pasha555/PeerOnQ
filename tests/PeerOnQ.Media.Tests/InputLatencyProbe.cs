using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Security;
using Xunit.Abstractions;

namespace PeerOnQ.Media.Tests;

/// <summary>
/// Test-only monotonic boundary timing. Forwards every production call unchanged and never
/// inspects plaintext input values, changes wire records, or substitutes acknowledgement times.
/// The acceptance fixture uses a fake sink; these are not Windows SendInput measurements.
/// </summary>
internal sealed class InputLatencyProbe
{
    private readonly ConcurrentDictionary<long, Sample> _commands = new();
    private Sample? _active;
    private readonly AsyncLocal<Sample?> _acknowledged = new();

    internal sealed class Sample
    {
        public long EventAt, CommandAt, ProtectedAt, AdmissionAt, ReceivedAt, DecryptedAt;
        public long InjectionEnteredAt, InjectedAt, AcknowledgedAt;
        public byte[]? ProtectedRecord;
        public bool AcknowledgementRequested;
    }

    public Sample Begin()
    {
        var sample = new Sample { EventAt = Stopwatch.GetTimestamp() };
        Volatile.Write(ref _active, sample);
        return sample;
    }

    public void InjectionEntered()
    {
        if (Volatile.Read(ref _active) is { } sample)
            Volatile.Write(ref sample.InjectionEnteredAt, Stopwatch.GetTimestamp());
    }

    public void Injected(long timestamp)
    {
        if (Volatile.Read(ref _active) is { } sample)
            Volatile.Write(ref sample.InjectedAt, timestamp);
    }

    public IMediaSession ObserveMedia(WebRtcMediaSession media, bool sender)
    {
        if (!sender)
        {
            // Register before MediaCollaborationTransport so the timestamp precedes AEAD work.
            media.DataMessageReceived += (_, payload) =>
            {
                if (Volatile.Read(ref _active) is { } sample
                    && Volatile.Read(ref sample.ProtectedRecord) is { } record
                    && payload.Span.SequenceEqual(record))
                    Volatile.Write(ref sample.ReceivedAt, Stopwatch.GetTimestamp());
            };
        }
        return InputLatencyBoundaryProxy<IMediaSession>.Wrap(media, (method, arguments) =>
        {
            if (sender && method == nameof(IMediaSession.SendDataAsync)
                && arguments?[0] is ReadOnlyMemory<byte> payload
                && arguments[2] is DataMessagePriority.Interactive
                && SessionTrafficProtector.TryReadRoutingContext(payload.Span, out var channel, out _)
                && channel == SecureChannelKind.Input
                && Volatile.Read(ref _active) is { } sample)
            {
                Volatile.Write(ref sample.ProtectedRecord, payload.ToArray());
                Volatile.Write(ref sample.ProtectedAt, Stopwatch.GetTimestamp());
                return () => Volatile.Write(ref sample.AdmissionAt, Stopwatch.GetTimestamp());
            }
            return null;
        });
    }

    public ICollaborationTransport ObserveInputTransport(MediaCollaborationTransport transport, bool sender)
    {
        // Register before RemoteInputSession to delimit decryption/binding from input validation.
        transport.MessageReceived += (_, message) =>
        {
            if (!sender && message is RemoteInputCommand command
                && _commands.TryGetValue(command.Sequence, out var sample))
                Volatile.Write(ref sample.DecryptedAt, Stopwatch.GetTimestamp());
            if (sender && message is RemoteInputAcknowledgement acknowledgement)
            {
                _commands.TryGetValue(acknowledgement.AcknowledgedSequence, out var acknowledged);
                _acknowledged.Value = acknowledged;
            }
        };
        return InputLatencyBoundaryProxy<ICollaborationTransport>.Wrap(transport, (method, arguments) =>
        {
            if (sender && method == nameof(ICollaborationTransport.SendAsync)
                && arguments?[0] is RemoteInputCommand command
                && Volatile.Read(ref _active) is { } sample)
            {
                Volatile.Write(ref sample.CommandAt, Stopwatch.GetTimestamp());
                sample.AcknowledgementRequested = command.MeasurementSentAtUnixMicroseconds > 0;
                _commands[command.Sequence] = sample;
            }
            if (sender && method == nameof(ICollaborationTransport.ReportInputLatency)
                && _acknowledged.Value is { } acknowledged)
                return () => Volatile.Write(ref acknowledged.AcknowledgedAt, Stopwatch.GetTimestamp());
            return null;
        });
    }

    public static async Task WaitForAcknowledgementsAsync(IReadOnlyList<Sample> samples)
    {
        var started = Stopwatch.GetTimestamp();
        while (samples.Any(sample => sample.AcknowledgementRequested && Volatile.Read(ref sample.AcknowledgedAt) == 0)
               && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            await Task.Delay(10);
    }

    public static void Write(ITestOutputHelper output, string scenario, IReadOnlyList<Sample> samples)
    {
        var expected = samples.Count(sample => sample.AcknowledgementRequested);
        var received = samples.Count(sample => Volatile.Read(ref sample.AcknowledgedAt) > 0);
        output.WriteLine($"latency scenario={scenario} authenticated_ack requested={expected} validated={received} unavailable={expected - received}");
        WriteStage("event_to_injection", sample => (sample.EventAt, sample.InjectedAt));
        WriteStage("pacing_and_command", sample => (sample.EventAt, sample.CommandAt));
        WriteStage("secure_record_creation", sample => (sample.CommandAt, sample.ProtectedAt));
        WriteStage("send_admission", sample => (sample.ProtectedAt, sample.AdmissionAt));
        // SendDataAsync returning is the public admission boundary, not a socket-write hook.
        // Concurrent delivery can precede that return; retain and disclose any overlap.
        WriteStage("send_return_to_delivery", sample => (sample.AdmissionAt, sample.ReceivedAt));
        WriteStage("secure_validation_and_decryption", sample => (sample.ReceivedAt, sample.DecryptedAt));
        WriteStage("permission_focus_rate_validation", sample => (sample.DecryptedAt, sample.InjectionEnteredAt));
        WriteStage("fake_sink_injection", sample => (sample.InjectionEnteredAt, sample.InjectedAt));
        WriteStage("authenticated_ack_return", sample => (sample.InjectedAt, sample.AcknowledgedAt));

        void WriteStage(string name, Func<Sample, (long Start, long End)> boundary)
        {
            var times = samples.Select(boundary).Where(pair => pair.Start > 0 && pair.End > 0)
                .Select(pair => Stopwatch.GetElapsedTime(pair.Start, pair.End).TotalMilliseconds)
                .Order().ToArray();
            if (times.Length == 0)
            {
                output.WriteLine($"latency scenario={scenario} stage={name} n=0 unavailable");
                return;
            }
            double Percentile(double fraction) => times[(int)Math.Ceiling(times.Length * fraction) - 1];
            output.WriteLine($"latency scenario={scenario} stage={name} n={times.Length} " +
                             $"p50={Percentile(0.50):F3}ms p95={Percentile(0.95):F3}ms p99={Percentile(0.99):F3}ms overlaps={times.Count(time => time < 0)}");
        }
    }
}

/// <summary>Test-only forwarding proxy; reflection observes public abstraction boundaries only.</summary>
public class InputLatencyBoundaryProxy<T> : DispatchProxy where T : class
{
    private T _target = null!;
    private Func<string, object?[]?, Action?> _before = null!;

    internal static T Wrap(T target, Func<string, object?[]?, Action?> before)
    {
        var proxy = Create<T, InputLatencyBoundaryProxy<T>>();
        var forwarding = (InputLatencyBoundaryProxy<T>)(object)proxy;
        forwarding._target = target;
        forwarding._before = before;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var after = _before(targetMethod.Name, args);
        object? result;
        try
        {
            result = targetMethod.Invoke(_target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        if (after is null) return result;
        if (result is Task task) return CompleteAsync(task, after);
        after();
        return result;
    }

    private static async Task CompleteAsync(Task task, Action after)
    {
        await task;
        after();
    }
}
