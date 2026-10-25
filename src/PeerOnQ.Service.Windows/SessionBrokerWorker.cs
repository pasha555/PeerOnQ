using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using PeerOnQ.Shared.Contracts;

namespace PeerOnQ.Service.Windows;

public sealed class SessionBrokerWorker(SessionLeaseRegistry leases) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep the original first-instance handle alive until shutdown, preventing another
        // unprivileged process from acquiring the pipe name between clients.
        var pipes = new List<NamedPipeServerStream>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task[] listeners = [];
        try
        {
            for (var index = 0; index < 16; index++) pipes.Add(BrokerNative.CreateServer(firstInstance: index == 0));
            listeners = pipes.Select(pipe => ServeInstanceAsync(pipe, lifetime.Token)).ToArray();
            // A failed listener terminates the broker instead of leaving an apparently
            // running SCM process with a partly broken pipe endpoint.
            var completed = await Task.WhenAny(listeners);
            await completed;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { Environment.ExitCode = 1; throw; }
        finally
        {
            await lifetime.CancelAsync();
            foreach (var pipe in pipes) pipe.Dispose();
            try { await Task.WhenAll(listeners); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task ServeInstanceAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(stoppingToken);
            try { await HandleAsync(pipe, stoppingToken); }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        SessionPeer? peer = null;
        Guid? lease = null;
        try
        {
            var packet = new byte[WindowsSessionBrokerProtocol.PacketSize];
            var registered = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(registered ? WindowsSessionBrokerProtocol.LeaseSeconds : 3));
                // Read first: Windows permits client impersonation only after a successful read.
                await pipe.ReadExactlyAsync(packet, deadline.Token);
                if (!WindowsSessionBrokerProtocol.TryReadRequest(packet, out var operation)) return;
                var current = BrokerNative.GetPeer(pipe);
                var accepted = false;
                if (!registered && operation == SessionBrokerOperation.Register)
                {
                    peer = current;
                    lease = leases.Register(current, DateTimeOffset.UtcNow);
                    registered = accepted = lease.HasValue;
                }
                else if (registered && (operation is SessionBrokerOperation.Heartbeat or SessionBrokerOperation.Unregister) && current == peer)
                {
                    accepted = leases.Heartbeat(current, lease!.Value, DateTimeOffset.UtcNow);
                }
                await pipe.WriteAsync(WindowsSessionBrokerProtocol.Response(accepted), deadline.Token);
                if (!accepted || operation == SessionBrokerOperation.Unregister) return;
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException) { }
        finally { if (peer is not null && lease.HasValue) leases.Remove(peer.SessionId, lease.Value); }
    }
}
