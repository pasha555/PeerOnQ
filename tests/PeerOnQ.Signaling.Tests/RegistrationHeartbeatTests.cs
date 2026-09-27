using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Errors;
using PeerOnQ.Signaling.Server.Registry;
using PeerOnQ.Transport;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class RegistrationHeartbeatTests
{
    [Fact]
    public async Task Pending_attestation_refresh_keeps_the_existing_connection_alive()
    {
        await using var harness = await SignalingHarness.StartAsync(options =>
        {
            options.ConnectionTokenLifetime = TimeSpan.FromSeconds(4);
            options.HeartbeatInterval = TimeSpan.FromMilliseconds(50);
            options.HeartbeatTimeout = TimeSpan.FromMilliseconds(500);
            options.SweepInterval = TimeSpan.FromMilliseconds(50);
        });
        using var device = new TestDevice("Delayed attestation");
        var attestation = new ControlledAttestationProvider();
        await using var client = CreateClient(harness, device, attestation);
        await client.ConnectAsync(device.Identity);
        var initialExpiry = client.TokenExpiresAt;
        var registry = harness.Service<DeviceRegistry>();
        Assert.True(registry.TryGet(device.Id, out var connection));

        await attestation.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            Assert.True(await Wait.UntilAsync(
                () => connection.LastSeen >= startedAt.AddMilliseconds(650),
                TimeSpan.FromSeconds(1.5)), "Application heartbeats stopped during cloud attestation refresh.");
            Assert.Equal(SignalingConnectionState.Registered, client.State);
            Assert.True(registry.TryGet(device.Id, out var currentConnection));
            Assert.Same(connection, currentConnection);
            Assert.Equal(initialExpiry, client.TokenExpiresAt);
            Assert.Equal(2, attestation.Requests);
        }
        finally
        {
            attestation.Release.TrySetResult(null);
        }

        Assert.True(await Wait.UntilAsync(() => client.TokenExpiresAt > initialExpiry, TimeSpan.FromSeconds(2)));
        Assert.True(registry.TryGet(device.Id, out var refreshedConnection));
        Assert.Same(connection, refreshedConnection);
    }

    [Fact]
    public async Task Pending_attestation_refresh_is_cancelled_when_the_existing_token_expires()
    {
        await using var harness = await SignalingHarness.StartAsync(options =>
            options.ConnectionTokenLifetime = TimeSpan.FromSeconds(2));
        using var device = new TestDevice("Expiring attestation");
        var attestation = new ControlledAttestationProvider();
        await using var client = CreateClient(harness, device, attestation);
        await client.ConnectAsync(device.Identity);
        var initialExpiry = client.TokenExpiresAt;
        await attestation.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.True(await Wait.UntilAsync(
                () => client.State != SignalingConnectionState.Registered,
                TimeSpan.FromSeconds(3)), "Refresh kept an expired registration active.");
            Assert.True(DateTimeOffset.UtcNow >= initialExpiry);
            await attestation.RefreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(initialExpiry, client.TokenExpiresAt);
            Assert.Equal(2, attestation.Requests);
        }
        finally
        {
            attestation.Release.TrySetResult(null);
        }
    }

    [Fact]
    public async Task Rejected_attestation_refresh_does_not_wait_for_the_existing_token_to_expire()
    {
        await using var harness = await SignalingHarness.StartAsync(options =>
            options.ConnectionTokenLifetime = TimeSpan.FromSeconds(4));
        using var device = new TestDevice("Rejected attestation");
        var attestation = new ControlledAttestationProvider();
        await using var client = CreateClient(harness, device, attestation);
        await client.ConnectAsync(device.Identity);
        var initialExpiry = client.TokenExpiresAt;
        await attestation.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        attestation.Release.TrySetException(new SignalingAuthenticationException("Identity rejected."));

        Assert.True(await Wait.UntilAsync(
            () => client.State != SignalingConnectionState.Registered,
            TimeSpan.FromSeconds(1)));
        Assert.True(DateTimeOffset.UtcNow < initialExpiry);
        Assert.Equal(initialExpiry, client.TokenExpiresAt);
        Assert.Equal(2, attestation.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_disposal_or_replacement_cancels_the_pending_refresh(bool replaceConnection)
    {
        await using var harness = await SignalingHarness.StartAsync(options =>
            options.ConnectionTokenLifetime = TimeSpan.FromSeconds(4));
        using var device = new TestDevice("Cancelled attestation");
        var attestation = new ControlledAttestationProvider();
        await using var client = CreateClient(harness, device, attestation);
        await client.ConnectAsync(device.Identity);
        var registry = harness.Service<DeviceRegistry>();
        Assert.True(registry.TryGet(device.Id, out var originalConnection));
        await attestation.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var transition = replaceConnection
            ? client.ConnectAsync(device.Identity)
            : client.DisposeAsync().AsTask();
        try
        {
            await attestation.RefreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            attestation.Release.TrySetResult(null);
        }
        await transition.WaitAsync(TimeSpan.FromSeconds(2));

        if (replaceConnection)
        {
            Assert.Equal(SignalingConnectionState.Registered, client.State);
            Assert.True(registry.TryGet(device.Id, out var currentConnection));
            Assert.NotSame(originalConnection, currentConnection);
            Assert.Equal(3, attestation.Requests);
        }
        else
        {
            Assert.Equal(SignalingConnectionState.Disconnected, client.State);
            Assert.Equal(2, attestation.Requests);
        }
    }

    private static WebSocketSignalingClient CreateClient(
        SignalingHarness harness,
        TestDevice device,
        ISignalingAttestationProvider attestation) => new(
            new SignalingClientOptions
            {
                ServerUri = harness.WebSocketUri,
                ClientCapabilities = TestClientCapabilities.All(),
                HeartbeatInterval = TimeSpan.FromMilliseconds(50),
                HeartbeatTimeout = TimeSpan.FromMilliseconds(500),
                AutoReconnect = false,
            },
            device,
            attestationProvider: attestation);

    private sealed class ControlledAttestationProvider : ISignalingAttestationProvider
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RefreshCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> GetSignalingAttestationAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1) return null;
            RefreshStarted.TrySetResult();
            try
            {
                return await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RefreshCancelled.TrySetResult();
                throw;
            }
        }
    }
}
