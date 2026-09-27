using System.Diagnostics;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Transport;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class LiveDockerFactAttribute : FactAttribute
{
    public LiveDockerFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PEERONQ_LIVE_DOCKER_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Run through the local Phase 3 Docker acceptance controller.";
        }
    }
}

public sealed class DockerRestartAcceptanceTests
{
    [LiveDockerFact]
    public async Task Signaling_restart_reauthenticates_but_does_not_resume_lost_server_state()
    {
        var signalingUri = new Uri(GetRequiredEnvironmentVariable("PEERONQ_LIVE_SIGNALING_URL"));
        var trustedDevelopmentRoot = File.ReadAllBytes(
            GetRequiredEnvironmentVariable("PEERONQ_LIVE_DEVELOPMENT_ROOT_CERTIFICATE"));
        using var viewer = new TestDevice("Docker Restart Viewer");
        using var sharer = new TestDevice("Docker Restart Sharer");
        await using var viewerClient = CreateReconnectingClient(viewer, signalingUri, trustedDevelopmentRoot);
        await using var sharerClient = CreateReconnectingClient(sharer, signalingUri, trustedDevelopmentRoot);

        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var originalSession = await EstablishViewOnlySessionAsync(viewerClient, sharerClient, sharer.Id);

        await RestartSignalingContainerAsync();

        Assert.True(await Wait.UntilAsync(
            () => viewerClient.State == SignalingConnectionState.Registered
                  && sharerClient.State == SignalingConnectionState.Registered,
            TimeSpan.FromSeconds(45)));

        var staleResume = await viewerClient.ResumeSessionAsync(originalSession);
        Assert.False(staleResume.Resumed);

        var replacementSession = await EstablishViewOnlySessionAsync(viewerClient, sharerClient, sharer.Id);
        Assert.NotEqual(originalSession, replacementSession);
    }

    private static WebSocketSignalingClient CreateReconnectingClient(
        TestDevice device,
        Uri signalingUri,
        byte[] trustedDevelopmentRoot) =>
        new(
            new SignalingClientOptions
            {
                ServerUri = signalingUri,
                TrustedDevelopmentRootCertificate = trustedDevelopmentRoot,
                ClientCapabilities = TestClientCapabilities.All(),
                HeartbeatInterval = TimeSpan.FromSeconds(2),
                AutoReconnect = true,
                ReconnectDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectDelay = TimeSpan.FromSeconds(2),
                MaxReconnectWindow = TimeSpan.FromSeconds(45),
                MaxReconnectAttempts = 30,
            },
            device);

    private static async Task<SessionId> EstablishViewOnlySessionAsync(
        WebSocketSignalingClient viewerClient,
        WebSocketSignalingClient sharerClient,
        PeerOnQ.Domain.Identity.PeerOnQId sharerId)
    {
        var incoming = new TaskCompletionSource<IncomingSessionNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new TaskCompletionSource<PermissionResultNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnIncoming(object? _, IncomingSessionNotification notification) => incoming.TrySetResult(notification);
        void OnPermission(object? _, PermissionResultNotification notification) => permission.TrySetResult(notification);

        sharerClient.SessionRequested += OnIncoming;
        viewerClient.PermissionResolved += OnPermission;
        try
        {
            var sessionId = SessionId.New();
            await viewerClient.RequestSessionAsync(sessionId, sharerId, SessionMode.ViewOnly);
            Assert.Equal(sessionId, (await Wait.ForAsync(incoming)).SessionId);
            await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
            var result = await Wait.ForAsync(permission);
            Assert.Equal(sessionId, result.SessionId);
            Assert.Equal(PermissionDecision.Accept, result.Decision);
            return sessionId;
        }
        finally
        {
            sharerClient.SessionRequested -= OnIncoming;
            viewerClient.PermissionResolved -= OnPermission;
        }
    }

    private static async Task RestartSignalingContainerAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GetRequiredEnvironmentVariable("PEERONQ_LIVE_DOCKER_EXE"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("--env-file");
        startInfo.ArgumentList.Add(GetRequiredEnvironmentVariable("PEERONQ_LIVE_COMPOSE_ENV_FILE"));
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(GetRequiredEnvironmentVariable("PEERONQ_LIVE_COMPOSE_FILE"));
        startInfo.ArgumentList.Add("restart");
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add("10");
        startInfo.ArgumentList.Add("signaling");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Docker restart process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(
            process.ExitCode == 0,
            $"Docker signaling restart failed with exit code {process.ExitCode}. {output} {error}");
    }

    private static string GetRequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing required Docker acceptance setting: {name}.");
}
