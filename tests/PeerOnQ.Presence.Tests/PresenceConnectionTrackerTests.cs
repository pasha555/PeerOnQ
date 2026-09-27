using PeerOnQ.Presence.Server;

namespace PeerOnQ.Presence.Tests;

public sealed class PresenceConnectionTrackerTests
{
    [Fact]
    public void Heartbeats_AreBoundedAndConnectionsAreRemoved()
    {
        var tracker = new PresenceConnectionTracker();
        var now = DateTimeOffset.Parse("2026-08-11T10:00:00Z");
        tracker.Register("connection-1", Guid.NewGuid(), now);

        Assert.Equal(1, tracker.Count);
        Assert.Equal(["connection-1"], tracker.SnapshotConnectionIds());
        Assert.False(tracker.AllowHeartbeat("connection-1", now.AddSeconds(2)));
        Assert.True(tracker.AllowHeartbeat("connection-1", now.AddSeconds(3)));
        Assert.False(tracker.AllowHeartbeat("connection-1", now.AddSeconds(4)));
        Assert.False(tracker.AllowHeartbeat("unknown", now.AddMinutes(1)));

        tracker.Remove("connection-1");
        Assert.Equal(0, tracker.Count);
        Assert.Empty(tracker.SnapshotConnectionIds());
    }

    [Fact]
    public void Options_RequireBoundedHeartbeatLeaseAndIdentity()
    {
        var validator = new PresenceOptionsValidator();
        var valid = new PresenceOptions
        {
            HeartbeatSeconds = 25,
            LeaseSeconds = 75,
            StaleSweepSeconds = 15,
            ServerId = "presence-az1-01",
            Region = "eu-central",
        };

        Assert.True(validator.Validate(null, valid).Succeeded);
        valid.LeaseSeconds = 30;
        valid.ServerId = string.Empty;
        Assert.True(validator.Validate(null, valid).Failed);
    }
}
