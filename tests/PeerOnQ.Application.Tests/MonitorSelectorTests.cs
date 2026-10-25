using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

/// <summary>
/// The viewer's monitor selector. A selection is a request the sharer may honour by switching
/// between its own displays; it can never reach anything else on the sharer's machine.
/// </summary>
public class MonitorSelectorTests
{
    private static readonly PeerOnQId Peer = PeerOnQId.Parse("LNK-483-921-756-204");

    private sealed record Harness(
        SessionCoordinator Coordinator,
        FakeSignalingClient Signaling,
        FakeMediaEngine Media,
        FakeCaptureSource Capture);

    private static Harness Build()
    {
        var signaling = new FakeSignalingClient();
        var media = new FakeMediaEngine();
        var capture = new FakeCaptureSource();

        var coordinator = new SessionCoordinator(
            signaling, media, new ScriptedPermissionPrompt(PermissionDecision.Accept),
            new InMemoryBlockedDeviceStore(), new InMemoryAuditLog(), () => capture,
            collaborationTransportFactory: (session, permissions, _) =>
                new FakeSecureCollaborationTransport(session, permissions))
        {
            PreferredCaptureTarget = capture.AvailableTargets[0],
        };

        return new Harness(coordinator, signaling, media, capture);
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task The_sharer_publishes_its_display_list_when_sharing_starts()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));

        Assert.True(await UntilAsync(() => h.Signaling.PublishedDisplays.Count > 0));

        var published = h.Signaling.PublishedDisplays[0];
        Assert.Equal(sessionId, published.Id);
        Assert.Equal(2, published.Displays.Count);
        Assert.Equal(h.Capture.AvailableTargets[0].Id, published.Active);
    }

    [Fact]
    public async Task A_viewer_selection_switches_the_shared_display_and_republishes()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Signaling.PublishedDisplays.Count > 0));

        var second = h.Capture.AvailableTargets[1];
        h.Signaling.RaiseDisplaySelection(sessionId, second.Id);

        Assert.True(await UntilAsync(() => h.Capture.SwitchedTargets.Count == 1));
        Assert.Equal(second.Id, h.Capture.SwitchedTargets[0].Id);

        // The viewer is told which screen is live now.
        Assert.True(await UntilAsync(() => h.Signaling.PublishedDisplays.Count == 2));
        Assert.Equal(second.Id, h.Signaling.PublishedDisplays[1].Active);
    }

    [Fact]
    public async Task An_unknown_display_id_is_ignored()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Signaling.PublishedDisplays.Count > 0));

        h.Signaling.RaiseDisplaySelection(sessionId, @"C:\Windows\System32");
        h.Signaling.RaiseDisplaySelection(sessionId, "not-a-display");

        await Task.Delay(200);

        Assert.Empty(h.Capture.SwitchedTargets);
        Assert.Single(h.Signaling.PublishedDisplays);
    }

    [Fact]
    public async Task A_selection_for_an_unknown_session_does_nothing()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        h.Signaling.RaiseDisplaySelection(SessionId.New(), h.Capture.AvailableTargets[1].Id);

        await Task.Delay(200);
        Assert.Empty(h.Capture.SwitchedTargets);
    }

    [Fact]
    public async Task A_viewer_never_acts_on_a_selection_request_aimed_at_it()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        // This device is the viewer in this session.
        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept);
        Assert.True(await UntilAsync(() => h.Media.Viewer is not null));

        h.Signaling.RaiseDisplaySelection(sessionId, h.Capture.AvailableTargets[1].Id);

        await Task.Delay(200);
        Assert.Empty(h.Capture.SwitchedTargets);
    }

    [Fact]
    public async Task The_viewer_surfaces_the_remote_display_list()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);

        RemoteDisplaysNotification? seen = null;
        h.Coordinator.RemoteDisplaysChanged += (_, e) => seen = e;

        var displays = new[]
        {
            new CaptureTargetInfo(CaptureTargetKind.Display, "remote-1", "Remote 1", 1920, 1080),
            new CaptureTargetInfo(CaptureTargetKind.Display, "remote-2", "Remote 2", 1280, 1024),
        };

        h.Signaling.RaiseDisplays(sessionId, displays, "remote-1");

        Assert.True(await UntilAsync(() => seen is not null));
        Assert.Equal(2, seen!.Displays.Count);
        Assert.Equal("remote-1", seen.ActiveDisplayId);
    }

    [Fact]
    public async Task The_viewer_can_request_another_display()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);

        await h.Coordinator.RequestRemoteDisplayAsync(sessionId, "remote-2");

        var request = Assert.Single(h.Signaling.DisplayRequests);
        Assert.Equal(sessionId, request.Id);
        Assert.Equal("remote-2", request.DisplayId);
    }

    [Fact]
    public async Task A_sharer_does_not_send_display_requests()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Signaling.PublishedDisplays.Count > 0));

        await h.Coordinator.RequestRemoteDisplayAsync(sessionId, "remote-2");

        Assert.Empty(h.Signaling.DisplayRequests);
    }
}
