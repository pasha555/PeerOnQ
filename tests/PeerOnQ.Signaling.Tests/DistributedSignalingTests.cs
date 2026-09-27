using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Signaling.Server;
using PeerOnQ.Signaling.Server.Registry;
using PeerOnQ.Signaling.Server.Security;
using PeerOnQ.Signaling.Server.Sessions;
using PeerOnQ.Transport.Protocol;
using PeerOnQ.Transport;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class RedisSignalingIntegrationFactAttribute : FactAttribute
{
    public RedisSignalingIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PEERONQ_TEST_REDIS")))
            Skip = "Set PEERONQ_TEST_REDIS to an isolated Redis endpoint.";
    }
}

public sealed class DistributedSignalingConfigurationTests
{
    [Fact]
    public void Cluster_mode_fails_closed_without_redis_or_safe_lease_settings()
    {
        var options = new SignalingOptions
        {
            Cluster = new SignalingClusterOptions
            {
                Enabled = true,
                InstanceId = "invalid instance id",
                RedisConnectionString = string.Empty,
                KeyPrefix = "peeronq:signaling",
                DeviceLeaseDuration = TimeSpan.FromSeconds(10),
                DeviceLeaseRefreshInterval = TimeSpan.FromSeconds(9),
            },
        };

        var result = new SignalingOptionsValidator(new TestEnvironment()).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("InstanceId", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("RedisConnectionString", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("KeyPrefix", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("DeviceLeaseDuration", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("DeviceLeaseRefreshInterval", StringComparison.Ordinal));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "PeerOnQ.Signaling.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class DistributedSignalingIntegrationTests
{
    [RedisSignalingIntegrationFact]
    [Trait("Category", "ExternalIntegration")]
    public async Task Two_nodes_atomically_share_resume_challenge_and_replay_state()
    {
        var redis = Environment.GetEnvironmentVariable("PEERONQ_TEST_REDIS")!;

        var prefix = $"peeronq:{{signaling}}:test:{Guid.NewGuid():N}";
        await using var nodeA = await StartNodeAsync("state-a", redis, prefix);
        await using var nodeB = await StartNodeAsync("state-b", redis, prefix);
        var viewer = PeerOnQ.Domain.Identity.PeerOnQId.Parse("LNK-111-222-333-444");
        var sharer = PeerOnQ.Domain.Identity.PeerOnQId.Parse("LNK-555-666-777-888");
        var sessionId = SessionId.New().ToString();
        var storeA = nodeA.Service<ISessionStore>();
        var storeB = nodeB.Service<ISessionStore>();

        var created = await storeA.TryCreateAsync(sessionId, viewer, sharer, "view-only");
        Assert.True(created.Created);
        await storeA.BindOwnerAsync(sessionId, viewer, "viewer-old");
        Assert.True(await storeB.TryTransitionAsync(
            sessionId, ServerSessionState.AwaitingPermission, ServerSessionState.Negotiating));
        var issued = await storeA.IssueResumeTokenAsync(sessionId, viewer, SignalingProtocol.CurrentVersion);
        Assert.NotNull(await storeA.TryResumeAsync(
            sessionId, viewer, issued.Token, "viewer-new", SignalingProtocol.CurrentVersion));
        Assert.Null(await storeB.TryResumeAsync(
            sessionId, viewer, issued.Token, "viewer-replay", SignalingProtocol.CurrentVersion));

        var challengeA = nodeA.Service<IUnattendedChallengeStore>();
        var challengeB = nodeB.Service<IUnattendedChallengeStore>();
        Assert.True(await challengeA.TryCreateAsync("request-1", viewer, sharer, TimeSpan.FromSeconds(30)));
        Assert.NotNull(await challengeB.TryConsumeAsync("request-1", sharer, viewer));
        Assert.Null(await challengeA.TryConsumeAsync("request-1", sharer, viewer));

        var replayA = nodeA.Service<ISessionRequestReplayGuard>();
        var replayB = nodeB.Service<ISessionRequestReplayGuard>();
        Assert.True(await replayA.TryRegisterAsync(viewer.Value, "nonce-1"));
        Assert.False(await replayB.TryRegisterAsync(viewer.Value, "nonce-1"));
        await storeB.RemoveAsync(sessionId);

        var orphanSessionId = SessionId.New().ToString();
        Assert.True((await storeA.TryCreateAsync(orphanSessionId, viewer, sharer, "view-only")).Created);
        const string lostConnectionId = "lost-connection";
        await storeA.BindOwnerAsync(orphanSessionId, viewer, lostConnectionId);
        var route = new DeviceRoute(
            viewer.Value,
            "state-a",
            lostConnectionId,
            "Expired Viewer",
            "sha256:test",
            null,
            0,
            string.Empty,
            new ClientCapabilityManifest
            {
                Platform = PeerOnQClientPlatforms.Windows,
                Capabilities = [EndpointCapabilityNames.ScreenRender, EndpointCapabilityNames.SessionViewer],
            });
        var backplaneA = nodeA.Service<ISignalingBackplane>();
        var backplaneB = nodeB.Service<ISignalingBackplane>();
        await backplaneA.RegisterOwnerAsync(route, CancellationToken.None);

        var database = nodeA.Service<IConnectionMultiplexer>().GetDatabase();
        await database.KeyDeleteAsync($"{prefix}:device:{viewer.Value}");
        await database.SortedSetAddAsync(
            $"{prefix}:device-owner-expiry",
            $"{viewer.Value}|state-a|{lostConnectionId}",
            0);
        var expired = Assert.Single(await backplaneB.TakeExpiredOwnersAsync(16, CancellationToken.None));
        Assert.Equal(viewer.Value, expired.DeviceId);
        Assert.Null(await backplaneB.ResolveOwnerAsync(viewer.Value, CancellationToken.None));
        Assert.Single(await storeB.MarkDisconnectedAsync(viewer, expired.ConnectionId));
        await storeB.RemoveAsync(orphanSessionId);

        var concurrentCreates = Enumerable.Range(0, 80)
            .Select(index => (Store: index % 2 == 0 ? storeA : storeB, Id: SessionId.New().ToString()))
            .ToArray();
        var createResults = await Task.WhenAll(concurrentCreates.Select(async item =>
            (item.Id, Result: await item.Store.TryCreateAsync(item.Id, viewer, sharer, "view-only"))));
        Assert.Equal(64, createResults.Count(item => item.Result.Created));
        Assert.Equal(64, await storeA.CountAsync());
        foreach (var item in createResults.Where(item => item.Result.Created))
            Assert.True(await storeB.RemoveAsync(item.Id));
        Assert.Equal(0, await storeA.CountAsync());
    }

    [RedisSignalingIntegrationFact]
    [Trait("Category", "ExternalIntegration")]
    public async Task Two_nodes_share_ownership_and_route_a_complete_session()
    {
        var redis = Environment.GetEnvironmentVariable("PEERONQ_TEST_REDIS")!;

        var prefix = $"peeronq:{{signaling}}:test:{Guid.NewGuid():N}";
        await using var nodeA = await StartNodeAsync("signal-a", redis, prefix);
        await using var nodeB = await StartNodeAsync("signal-b", redis, prefix);
        using var viewer = new TestDevice("Distributed Viewer");
        using var sharer = new TestDevice("Distributed Sharer");
        await using var viewerClient = viewer.CreateClient(nodeA.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(nodeB.WebSocketUri);

        var incoming = new TaskCompletionSource<IncomingSessionNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new TaskCompletionSource<PermissionResultNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var offer = new TaskCompletionSource<SdpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<SessionEndedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SessionRequested += (_, value) => incoming.TrySetResult(value);
        viewerClient.PermissionResolved += (_, value) => permission.TrySetResult(value);
        sharerClient.SdpReceived += (_, value) => offer.TrySetResult(value);
        sharerClient.SessionEnded += (_, value) => ended.TrySetResult(value);

        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        Assert.True(await nodeA.Service<DeviceRegistry>().IsOnlineAsync(sharer.Id));
        Assert.True(await nodeB.Service<DeviceRegistry>().IsOnlineAsync(viewer.Id));

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        Assert.Equal(sessionId, (await Wait.ForAsync(incoming)).SessionId);
        Assert.Equal(1, await nodeA.Service<ISessionStore>().CountAsync());
        Assert.Equal(1, await nodeB.Service<ISessionStore>().CountAsync());

        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        Assert.Equal(PermissionDecision.Accept, (await Wait.ForAsync(permission)).Decision);
        await viewerClient.SendSdpAsync(sessionId, "offer", "v=0 distributed-offer");
        Assert.Equal("v=0 distributed-offer", (await Wait.ForAsync(offer)).Sdp);

        await viewerClient.EndSessionAsync(sessionId, SessionEndReason.EndedByViewer);
        Assert.Equal(SessionEndReason.EndedByViewer, (await Wait.ForAsync(ended)).Reason);
        Assert.Equal(0, await nodeA.Service<ISessionStore>().CountAsync());
    }

    [RedisSignalingIntegrationFact]
    [Trait("Category", "ExternalIntegration")]
    public async Task Active_session_reconnects_and_resumes_on_the_surviving_node()
    {
        var redis = Environment.GetEnvironmentVariable("PEERONQ_TEST_REDIS")!;
        var prefix = $"peeronq:{{signaling}}:test:{Guid.NewGuid():N}";
        await using var nodeA = await StartNodeAsync("failover-a", redis, prefix);
        await using var nodeB = await StartNodeAsync("failover-b", redis, prefix);
        await using var proxy = new TcpFailoverProxy(nodeA.WebSocketUri, nodeB.WebSocketUri);
        using var viewer = new TestDevice("Failover Viewer");
        using var sharer = new TestDevice("Failover Sharer");
        await using var viewerClient = CreateReconnectingClient(viewer, proxy.WebSocketUri);
        await using var sharerClient = CreateReconnectingClient(sharer, proxy.WebSocketUri);

        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var incoming = new TaskCompletionSource<IncomingSessionNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new TaskCompletionSource<PermissionResultNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SessionRequested += (_, value) => incoming.TrySetResult(value);
        viewerClient.PermissionResolved += (_, value) => permission.TrySetResult(value);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        Assert.Equal(sessionId, (await Wait.ForAsync(incoming)).SessionId);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        Assert.Equal(PermissionDecision.Accept, (await Wait.ForAsync(permission)).Decision);
        var initialOffer = new TaskCompletionSource<SdpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SdpReceived += (_, value) => initialOffer.TrySetResult(value);
        await viewerClient.SendSdpAsync(sessionId, "offer", "v=0 initial-offer");
        Assert.Equal("v=0 initial-offer", (await Wait.ForAsync(initialOffer)).Sdp);
        Assert.Equal(ServerSessionState.Active,
            (await nodeB.Service<ISessionStore>().GetAsync(sessionId.ToString()))?.State);
        Assert.True(await nodeA.Service<ISessionStore>().IsOwnedByAsync(
            sessionId.ToString(), viewer.Id, (await nodeA.Service<ISignalingBackplane>()
                .ResolveOwnerAsync(viewer.Id.Value, CancellationToken.None))!.ConnectionId));

        proxy.RouteNewConnectionsOnlyToSecondNode();
        await nodeA.DisposeAsync();
        Assert.True(await WaitForOwnerAsync(
            nodeB.Service<ISignalingBackplane>(), viewer.Id.Value, "failover-b", TimeSpan.FromSeconds(10)));
        Assert.Equal(SignalingConnectionState.Registered, viewerClient.State);

        var peerResumed = new TaskCompletionSource<SessionPeerResumedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.PeerResumed += (_, value) => peerResumed.TrySetResult(value);
        var resumed = await viewerClient.ResumeSessionAsync(sessionId);
        Assert.True(resumed.Resumed, resumed.Reason);
        Assert.Equal(sessionId, (await Wait.ForAsync(peerResumed)).SessionId);

        var relayed = new TaskCompletionSource<SdpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SdpReceived += (_, value) => relayed.TrySetResult(value);
        await viewerClient.SendSdpAsync(sessionId, "offer", "v=0 failover-offer");
        Assert.Equal("v=0 failover-offer", (await Wait.ForAsync(relayed)).Sdp);
        var ended = new TaskCompletionSource<SessionEndedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SessionEnded += (_, value) => ended.TrySetResult(value);
        await viewerClient.EndSessionAsync(sessionId, SessionEndReason.EndedByViewer);
        Assert.Equal(SessionEndReason.EndedByViewer, (await Wait.ForAsync(ended)).Reason);
        Assert.Equal(0, await nodeB.Service<ISessionStore>().CountAsync());
    }

    private static WebSocketSignalingClient CreateReconnectingClient(TestDevice device, Uri signalingUri) =>
        new(
            new SignalingClientOptions
            {
                ServerUri = signalingUri,
                ClientCapabilities = TestClientCapabilities.All(),
                HeartbeatInterval = TimeSpan.FromSeconds(2),
                AutoReconnect = true,
                ReconnectDelay = TimeSpan.FromMilliseconds(50),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(500),
                MaxReconnectWindow = TimeSpan.FromSeconds(10),
                MaxReconnectAttempts = 30,
            },
            device);

    private static async Task<bool> WaitForOwnerAsync(
        ISignalingBackplane backplane,
        string deviceId,
        string instanceId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var owner = await backplane.ResolveOwnerAsync(deviceId, CancellationToken.None);
            if (string.Equals(owner?.InstanceId, instanceId, StringComparison.Ordinal)) return true;
            await Task.Delay(25);
        }

        return false;
    }

    private static Task<SignalingHarness> StartNodeAsync(string instanceId, string redis, string prefix) =>
        SignalingHarness.StartAsync(
            configuration: new Dictionary<string, string>
            {
                ["Signaling:Cluster:Enabled"] = "true",
                ["Signaling:Cluster:InstanceId"] = instanceId,
                ["Signaling:Cluster:RedisConnectionString"] = redis,
                ["Signaling:Cluster:KeyPrefix"] = prefix,
                ["Signaling:Cluster:DeviceLeaseDuration"] = "00:00:30",
                ["Signaling:Cluster:DeviceLeaseRefreshInterval"] = "00:00:10",
            });

    private sealed class TcpFailoverProxy : IAsyncDisposable
    {
        private readonly IPEndPoint[] _backends;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly object _connectionsGate = new();
        private readonly List<Task> _connections = [];
        private readonly Task _acceptLoop;
        private int _acceptedConnections;
        private int _routeOnlyToSecondNode;

        public TcpFailoverProxy(params Uri[] backends)
        {
            _backends = backends.Select(uri => new IPEndPoint(IPAddress.Loopback, uri.Port)).ToArray();
            _listener.Start();
            WebSocketUri = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/ws");
            _acceptLoop = AcceptLoopAsync();
        }

        public Uri WebSocketUri { get; }

        public void RouteNewConnectionsOnlyToSecondNode() =>
            Volatile.Write(ref _routeOnlyToSecondNode, 1);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var incoming = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    var acceptedIndex = Interlocked.Increment(ref _acceptedConnections) - 1;
                    var index = Volatile.Read(ref _routeOnlyToSecondNode) == 1
                        ? 1
                        : acceptedIndex % _backends.Length;
                    var connection = ForwardAsync(incoming, _backends[index], _stopping.Token);
                    lock (_connectionsGate) _connections.Add(connection);
                    _ = connection.ContinueWith(
                        completed =>
                        {
                            lock (_connectionsGate) _connections.Remove(completed);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Expected during disposal.
            }
        }

        private static async Task ForwardAsync(
            TcpClient incoming,
            IPEndPoint backend,
            CancellationToken cancellationToken)
        {
            using (incoming)
            using (var outgoing = new TcpClient())
            {
                incoming.NoDelay = true;
                outgoing.NoDelay = true;
                await outgoing.ConnectAsync(backend.Address, backend.Port, cancellationToken);
                using var inboundStream = incoming.GetStream();
                using var outboundStream = outgoing.GetStream();
                var toBackend = inboundStream.CopyToAsync(outboundStream, cancellationToken);
                var toClient = outboundStream.CopyToAsync(inboundStream, cancellationToken);
                await Task.WhenAny(toBackend, toClient);
                incoming.Close();
                outgoing.Close();
                try
                {
                    await Task.WhenAll(toBackend, toClient);
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
                {
                    // One half closing terminates the paired forwarding operation.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (SocketException) when (_stopping.IsCancellationRequested)
            {
                // Expected when Stop interrupts AcceptTcpClientAsync.
            }

            Task[] connections;
            lock (_connectionsGate) connections = _connections.ToArray();
            try
            {
                await Task.WhenAll(connections);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
            {
                // Forwarders are cancelled as part of the bounded test teardown.
            }
            _stopping.Dispose();
        }
    }
}
