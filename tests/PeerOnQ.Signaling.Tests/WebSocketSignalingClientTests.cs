using System.Diagnostics;
using System.Net.WebSockets;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Transport;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class WebSocketSignalingClientTests
{
    [Fact]
    public void Heartbeat_deadline_uses_monotonic_time_and_expires_at_the_boundary()
    {
        var lastResponse = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(12);
        var beforeDeadline = lastResponse + (long)(timeout.TotalSeconds * Stopwatch.Frequency) - 1;
        var atDeadline = lastResponse + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        Assert.False(WebSocketSignalingClient.IsHeartbeatExpired(lastResponse, beforeDeadline, timeout));
        Assert.True(WebSocketSignalingClient.IsHeartbeatExpired(lastResponse, atDeadline, timeout));
        Assert.False(WebSocketSignalingClient.IsHeartbeatExpired(0, atDeadline, timeout));
    }

    [Fact]
    public void File_relay_processing_defers_a_queued_heartbeat_failure()
    {
        var lastResponse = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(12);
        var atDeadline = lastResponse + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        Assert.False(WebSocketSignalingClient.ShouldFailHeartbeat(
            lastResponse,
            atDeadline,
            timeout,
            fileRelayReceiveInProgress: true));
        Assert.True(WebSocketSignalingClient.ShouldFailHeartbeat(
            lastResponse,
            atDeadline,
            timeout,
            fileRelayReceiveInProgress: false));

        var completion = atDeadline;
        Assert.False(WebSocketSignalingClient.ShouldFailHeartbeat(
            lastResponse,
            completion,
            timeout,
            fileRelayReceiveInProgress: false,
            lastFileRelayReceiveCompletion: completion));
        Assert.True(WebSocketSignalingClient.ShouldFailHeartbeat(
            lastResponse,
            completion + (long)(timeout.TotalSeconds * Stopwatch.Frequency),
            timeout,
            fileRelayReceiveInProgress: false,
            lastFileRelayReceiveCompletion: completion));
    }

    [Fact]
    public void Registration_refresh_is_scheduled_before_expiry_with_a_bounded_lead()
    {
        var registeredAt = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var expiresAt = registeredAt.AddMinutes(10);

        var refreshAt = WebSocketSignalingClient.CalculateRegistrationRefreshAt(
            registeredAt,
            expiresAt,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(10));

        Assert.Equal(expiresAt.AddSeconds(-30), refreshAt);
        Assert.False(WebSocketSignalingClient.ShouldRefreshRegistration(refreshAt.UtcTicks, refreshAt.AddTicks(-1)));
        Assert.True(WebSocketSignalingClient.ShouldRefreshRegistration(refreshAt.UtcTicks, refreshAt));
    }

    [Fact]
    public void Recoverable_token_expiry_is_not_surfaced_as_a_fatal_user_error()
    {
        Assert.False(WebSocketSignalingClient.ShouldSurfaceSignalingError("token_expired"));
        Assert.True(WebSocketSignalingClient.ShouldSurfaceSignalingError("invalid_proof"));
    }

    [Theory]
    [InlineData(SignalingConnectionState.Registered, false, false, true)]
    [InlineData(SignalingConnectionState.Registered, true, true, true)]
    [InlineData(SignalingConnectionState.Faulted, true, false, true)]
    [InlineData(SignalingConnectionState.Faulted, false, true, true)]
    [InlineData(SignalingConnectionState.Reconnecting, true, true, false)]
    [InlineData(SignalingConnectionState.Disconnected, true, true, false)]
    public void Network_changes_refresh_only_live_or_recoverable_signaling_states(
        SignalingConnectionState state,
        bool networkAvailable,
        bool addressChanged,
        bool expected)
    {
        Assert.Equal(
            expected,
            WebSocketSignalingClient.ShouldReconnectForNetworkChange(
                state,
                networkAvailable,
                addressChanged));
    }

    [Theory]
    [InlineData(WebSocketCloseStatus.NormalClosure, "replaced_by_new_connection", false)]
    [InlineData(WebSocketCloseStatus.NormalClosure, "server_restarting", true)]
    [InlineData(WebSocketCloseStatus.EndpointUnavailable, "network_change_test", true)]
    public void Only_an_intentional_connection_replacement_stops_automatic_reconnect(
        WebSocketCloseStatus closeStatus,
        string closeReason,
        bool expected)
    {
        Assert.Equal(
            expected,
            WebSocketSignalingClient.ShouldReconnectAfterServerClose(closeStatus, closeReason));
    }

    [Theory]
    [InlineData(false, false, false, true, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, false, false, false, false)]
    public void Reconnect_is_suppressed_after_terminal_instance_replacement(
        bool disposed,
        bool terminallyReplaced,
        bool cancellationRequested,
        bool ownsCurrentSocket,
        bool expected)
    {
        Assert.Equal(
            expected,
            WebSocketSignalingClient.ShouldScheduleReconnect(
                disposed,
                terminallyReplaced,
                cancellationRequested,
                ownsCurrentSocket));
    }
}
