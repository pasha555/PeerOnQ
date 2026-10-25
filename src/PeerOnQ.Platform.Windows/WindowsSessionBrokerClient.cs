using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PeerOnQ.Shared.Contracts;

namespace PeerOnQ.Platform.Windows;

/// <summary>
/// Registers the existing interactive host with the optional machine broker. Identity, network,
/// capture and input remain in this process, under the signed-in user's existing permissions.
/// </summary>
public sealed class WindowsSessionBrokerClient(ILogger<WindowsSessionBrokerClient> logger)
{
    public Task RunAsync(CancellationToken cancellationToken) => Task.Run(() => RunCoreAsync(cancellationToken), cancellationToken);

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        var warned = false;
        while (!cancellationToken.IsCancellationRequested && IsOptedIn())
        {
            try
            {
                using var pipe = ConnectVerified();
                if (!await ExchangeAsync(pipe, SessionBrokerOperation.Register, cancellationToken))
                    throw new IOException("The session broker rejected this interactive host.");
                warned = false;
                logger.LogInformation("The current-user host registered with the installed session broker.");
                while (!cancellationToken.IsCancellationRequested && IsOptedIn())
                {
                    await Task.Delay(TimeSpan.FromSeconds(WindowsSessionBrokerProtocol.HeartbeatSeconds), cancellationToken);
                    if (!await ExchangeAsync(pipe, SessionBrokerOperation.Heartbeat, cancellationToken))
                        throw new IOException("The session broker lease ended.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                // Broker availability never substitutes for the host's authorization or consent.
                if (!warned) logger.LogWarning("The optional session broker is unavailable; the current-user host keeps its existing security policy.");
                warned = true;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private static bool IsOptedIn()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(WindowsSessionBrokerProtocol.RegistryKey);
            return key?.GetValue("Enabled") is int value && value == 1;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    private static NamedPipeClientStream ConnectVerified()
    {
        var pipePath = @"\\.\pipe\" + WindowsSessionBrokerProtocol.PipeName;
        if (!WaitNamedPipe(pipePath, 1000)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Individual read/write rights exclude FILE_CREATE_PIPE_INSTANCE. Identification SQOS
        // lets LocalService inspect our SID, but prevents it from acting as the interactive user.
        var handle = CreateFile(pipePath, 0x120083, 0, IntPtr.Zero, 3, 0x40110000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            if (!GetNamedPipeServerProcessId(handle, out var serverPid) || !IsRunningServiceProcess(serverPid))
                throw new UnauthorizedAccessException("The local pipe is not owned by the installed SCM broker.");
            return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
        }
        catch { handle.Dispose(); throw; }
    }

    private static bool IsRunningServiceProcess(uint processId)
    {
        if (processId == 0 || !ProcessIdToSessionId(processId, out var session) || session != 0) return false;
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) return false;
        try
        {
            var service = OpenService(manager, WindowsSessionBrokerProtocol.ServiceName, 4);
            if (service == IntPtr.Zero) return false;
            try
            {
                return QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _) &&
                    status.State == 4 && status.ProcessId == processId;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static async Task<bool> ExchangeAsync(Stream pipe, SessionBrokerOperation operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await pipe.WriteAsync(WindowsSessionBrokerProtocol.Request(operation), deadline.Token);
            var packet = new byte[WindowsSessionBrokerProtocol.PacketSize];
            await pipe.ReadExactlyAsync(packet, deadline.Token);
            return WindowsSessionBrokerProtocol.IsAccepted(packet);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("The local session broker timed out."); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, Checkpoint, WaitHint, ProcessId, Flags;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int bufferSize, out int needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
