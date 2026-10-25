using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class ClockSyncRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_retries_after_late_or_uncertain_authenticated_reply_without_waiting_one_minute(bool lateReply)
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Clock viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Clock sharer");
        var clock = new DrivenClock();
        var sessionId = SessionId.New();
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer) { IsVideoFrameTelemetryNegotiated = true };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer) { IsVideoFrameTelemetryNegotiated = true };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        var replyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLateReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = 0;
        sharerMedia.BeforeSendData = async (payload, cancellationToken) =>
        {
            if (!SessionTrafficProtector.TryReadRoutingContext(payload.Span, out var channel, out _)
                || channel != SecureChannelKind.Telemetry) return;
            var number = Interlocked.Increment(ref replies);
            if (lateReply && number == 1)
            {
                replyEntered.TrySetResult();
                await releaseLateReply.Task.WaitAsync(cancellationToken);
            }
            else if (!lateReply && number <= 5)
            {
                // Simulate 120ms transport delay after the remote reply timestamp: uncertainty 60ms.
                clock.Advance(TimeSpan.FromMilliseconds(120));
            }
        };
        await using var viewer = new MediaCollaborationTransport(viewerMedia, SessionPermission.ViewScreen,
            viewerIdentity.Service, sharerIdentity.LegacyFingerprint, timeProvider: clock);
        await using var sharer = new MediaCollaborationTransport(sharerMedia, SessionPermission.ViewScreen,
            sharerIdentity.Service, viewerIdentity.LegacyFingerprint,
            timeProvider: new OffsetClock(clock, TimeSpan.FromMilliseconds(50)));
        var failures = new List<string>();
        viewer.ProtocolError += (_, code) => failures.Add(code);
        sharer.ProtocolError += (_, code) => failures.Add(code);
        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        try
        {
            if (lateReply)
            {
                await replyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await DriveAsync(clock, () => clock.OneSecondTimers > 0, TimeSpan.FromMilliseconds(100));
                clock.Advance(TimeSpan.FromMilliseconds(1100));
                await DriveAsync(clock, () => clock.OneSecondTimers >= 2, TimeSpan.FromSeconds(2));
                clock.Advance(TimeSpan.FromSeconds(1));
                releaseLateReply.TrySetResult();
            }
            else
            {
                await DriveAsync(clock, () => viewerMedia.PeerClockEstimate is not null, TimeSpan.FromSeconds(2));
                Assert.Equal(60_000, viewerMedia.PeerClockEstimate!.Value.UncertaintyMicroseconds);
            }
            await DriveAsync(clock, () => viewerMedia.PeerClockEstimate is { UncertaintyMicroseconds: <= 50_000 },
                TimeSpan.FromSeconds(5));
            var estimate = Assert.IsType<PeerClockEstimate>(viewerMedia.PeerClockEstimate);
            Assert.Equal(50_000, estimate.RemoteMinusLocalOffsetMicroseconds);
            Assert.InRange(estimate.UncertaintyMicroseconds, 0, 50_000);
            Assert.True(viewer.IsReady && sharer.IsReady);
            Assert.Empty(failures);
        }
        finally { releaseLateReply.TrySetResult(); }
    }

    private static async Task DriveAsync(DrivenClock clock, Func<bool> condition, TimeSpan maximumAdvance)
    {
        var deadline = clock.GetUtcNow() + maximumAdvance;
        while (!condition() && clock.GetUtcNow() < deadline)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Delay(1);
        }
        Assert.True(condition(), "Expected bounded clock-recovery state was not reached in simulated time.");
    }

    private sealed class OffsetClock(DrivenClock clock, TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow() + offset;
    }

    private sealed class DrivenClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<DrivenTimer> _timers = [];
        private long _ticks;
        private int _oneSecondTimers;
        public int OneSecondTimers => Volatile.Read(ref _oneSecondTimers);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddHours(1).AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DrivenTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _ticks, elapsed.Ticks);
            DrivenTimer[] due;
            lock (_gate) due = _timers.Where(timer => timer.TakeIfDue(GetTimestamp())).ToArray();
            foreach (var timer in due) timer.Fire();
        }
        private sealed class DrivenTimer(DrivenClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (owner._gate)
                {
                    _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetTimestamp() + dueTime.Ticks;
                    if (dueTime == TimeSpan.FromSeconds(1)) Interlocked.Increment(ref owner._oneSecondTimers);
                }
                return true;
            }
            public bool TakeIfDue(long now)
            {
                if (_due > now) return false;
                _due = long.MaxValue;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (owner._gate) { _due = long.MaxValue; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
