using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PeerOnQ.Service.Windows;
using PeerOnQ.Shared.Contracts;
using Xunit;

namespace PeerOnQ.Service.Windows.Tests;

public sealed class SessionBrokerTests
{
    private const string HostPath = @"C:\Program Files\PeerOnQ\PeerOnQ.exe";
    private static readonly SessionPeer Peer = new(42, 1, "S-1-5-21-100-200-300-1001", HostPath, true);

    [Theory]
    [InlineData(0, true, "S-1-5-21-100-200-300-1001", HostPath)]
    [InlineData(1, false, "S-1-5-21-100-200-300-1001", HostPath)]
    [InlineData(1, true, "S-1-5-18", HostPath)]
    [InlineData(1, true, "S-1-5-19", HostPath)]
    [InlineData(1, true, "S-1-5-20", HostPath)]
    [InlineData(1, true, "", HostPath)]
    [InlineData(1, true, "S-1-5-21-100-200-300-1001", @"C:\Users\User\fake.exe")]
    public void RegistrationRejectsNonInteractiveOrUninstalledHosts(int session, bool active, string sid, string executable)
    {
        var registry = new SessionLeaseRegistry(HostPath);
        Assert.Null(registry.Register(Peer with { SessionId = (uint)session, Active = active, UserSid = sid, ExecutablePath = executable }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void LeaseIsBoundToExactPeerExpiresAndCannotRemoveReplacement()
    {
        var registry = new SessionLeaseRegistry(HostPath);
        var now = DateTimeOffset.UtcNow;
        var first = registry.Register(Peer, now)!.Value;
        Assert.Null(registry.Register(Peer with { ProcessId = 43 }, now));
        Assert.False(registry.Heartbeat(Peer with { UserSid = "S-1-5-21-other" }, first, now));
        Assert.False(registry.Heartbeat(Peer with { ProcessId = 43 }, first, now));
        Assert.True(registry.Heartbeat(Peer, first, now.AddSeconds(10)));
        Assert.False(registry.Heartbeat(Peer, first, now.AddSeconds(40)));
        var replacement = registry.Register(Peer, now.AddSeconds(40))!.Value;
        registry.Remove(Peer.SessionId, first);
        Assert.True(registry.Heartbeat(Peer, replacement, now.AddSeconds(41)));
        registry.Remove(Peer.SessionId, replacement);
        Assert.False(registry.Heartbeat(Peer, replacement, now.AddSeconds(42)));
    }

    [Fact]
    public void PacketParserRejectsMalformedExtendedOrUnknownOperations()
    {
        var valid = WindowsSessionBrokerProtocol.Request(SessionBrokerOperation.Register);
        Assert.True(WindowsSessionBrokerProtocol.TryReadRequest(valid, out var operation));
        Assert.Equal(SessionBrokerOperation.Register, operation);
        Assert.False(WindowsSessionBrokerProtocol.TryReadRequest(valid.AsSpan(0, 7), out _));
        Assert.False(WindowsSessionBrokerProtocol.TryReadRequest([.. valid, 0], out _));
        valid[7] = 1;
        Assert.False(WindowsSessionBrokerProtocol.TryReadRequest(valid, out _));
        valid[7] = 0;
        valid[4] = 255;
        Assert.False(WindowsSessionBrokerProtocol.TryReadRequest(valid, out _));
        Assert.False(WindowsSessionBrokerProtocol.IsAccepted(WindowsSessionBrokerProtocol.Response(false)));
        Assert.True(WindowsSessionBrokerProtocol.IsAccepted(WindowsSessionBrokerProtocol.Response(true)));
    }

    [Fact]
    public void PipeAclDeniesNetworkAndDoesNotGrantUsersServerInstanceRights()
    {
        var descriptor = new RawSecurityDescriptor(BrokerNative.PipeDacl);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        var acl = descriptor.DiscretionaryAcl!;
        var userAce = acl.Cast<CommonAce>().Single(ace => ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.AuthenticatedUserSid));
        Assert.Equal(AceQualifier.AccessAllowed, userAce.AceQualifier);
        Assert.Equal(0, userAce.AccessMask & 4); // FILE_CREATE_PIPE_INSTANCE is also FILE_APPEND_DATA.
        Assert.Equal(3, userAce.AccessMask & 3);
        Assert.Contains(acl.Cast<CommonAce>(), ace => ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.NetworkSid) && ace.AceQualifier == AceQualifier.AccessDenied);
    }

    [Fact]
    public void BrokerFailsClosedWhenPipeNameAlreadyExists()
    {
        var name = "PeerOnQ.Broker.Test." + Guid.NewGuid().ToString("N");
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => BrokerNative.CreateServer(true, name));
    }

    [Fact]
    public async Task ActualLocalPipeReadsKernelProcessSessionAndWindowsIdentity()
    {
        var name = "PeerOnQ.Broker.Test." + Guid.NewGuid().ToString("N");
        using var server = BrokerNative.CreateServer(true, name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        var handle = CreateFile(@"\\.\pipe\" + name, 0x120083, 0, IntPtr.Zero, 3, 0x40110000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        using var client = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
        await connected;
        await client.WriteAsync(WindowsSessionBrokerProtocol.Request(SessionBrokerOperation.Register), deadline.Token);
        var packet = new byte[WindowsSessionBrokerProtocol.PacketSize];
        await server.ReadExactlyAsync(packet, deadline.Token);
        var peer = BrokerNative.GetPeer(server);
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal((uint)Environment.ProcessId, peer.ProcessId);
        Assert.Equal((uint)currentProcess.SessionId, peer.SessionId);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User!.Value, peer.UserSid);
        Assert.Equal(Environment.ProcessPath, peer.ExecutablePath, ignoreCase: true);
        await server.WriteAsync(WindowsSessionBrokerProtocol.Response(true), deadline.Token);
        await client.ReadExactlyAsync(packet, deadline.Token);
        Assert.True(WindowsSessionBrokerProtocol.IsAccepted(packet));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
