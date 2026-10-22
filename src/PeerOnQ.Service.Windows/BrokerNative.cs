using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PeerOnQ.Shared.Contracts;

namespace PeerOnQ.Service.Windows;

internal static class BrokerNative
{
    // Explicit read/write rights exclude FILE_CREATE_PIPE_INSTANCE (0x4). GENERIC_WRITE
    // would incorrectly grant callers the ability to create another server instance.
    internal const string PipeDacl = "D:P(D;;GA;;;NU)(A;;FA;;;SY)(A;;FA;;;LS)(A;;FA;;;BA)(A;;0x120083;;;AU)";

    internal static NamedPipeServerStream CreateServer(bool firstInstance, string pipeName = WindowsSessionBrokerProtocol.PipeName)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(PipeDacl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + pipeName,
                0x40000003u | (firstInstance ? 0x00080000u : 0u), 0x00000008, 16, 128, 128, 1000, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    internal static SessionPeer GetPeer(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ||
            !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var sessionId))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        string? sid = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true);
            sid = identity?.User?.Value;
        });
        if (sid is null) throw new UnauthorizedAccessException("The pipe has no authenticated Windows user.");
        using var process = OpenProcess(0x1000, false, pid);
        var path = new StringBuilder(32768);
        var pathLength = path.Capacity;
        if (process.IsInvalid || !QueryFullProcessImageName(process, 0, path, ref pathLength))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var active = false;
        if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, 8, out var buffer, out var length))
        {
            try { active = length >= 4 && Marshal.ReadInt32(buffer) == 0; }
            finally { WTSFreeMemory(buffer); }
        }
        return new SessionPeer(pid, sessionId, sid, path.ToString(), active);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr result, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int informationClass, out IntPtr buffer, out uint length);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
