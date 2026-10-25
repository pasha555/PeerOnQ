using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PeerOnQ.Platform.Windows.Rendering;

/// <summary>Who loaded a module: the loading thread and its native and managed callers.</summary>
public sealed record ModuleLoadTrace(
    string Module,
    int ManagedThreadId,
    uint OsThreadId,
    string ThreadName,
    string NativeCallers,
    string ManagedStack);

/// <summary>
/// Records the thread and call stack that loads one named DLL, through the loader's DLL
/// notification (LdrRegisterDllNotification). Field 0.9.86/0.9.87 PCs with hardware adapters
/// loaded the WARP rasterizer "after the main window was activated" and no sampler could say who
/// asked for it; this captures the requester at the moment of the load.
/// </summary>
/// <remarks>
/// The callback runs under the loader lock for every DLL the process loads. The common path only
/// compares the module name (no allocation). Everything the matching path uses is warmed up before
/// registration, so it does not JIT or create P/Invoke stubs under the lock; it copies the loading
/// thread's stack and the callers are resolved and reported on the thread pool. The registration
/// ends after the first match or on <see cref="Dispose"/>.
/// </remarks>
public sealed unsafe class ModuleLoadTracer : IDisposable
{
    private const uint LoadedReason = 1;
    private const int MaximumNativeCallers = 24;
    private const int StackCopyBytes = 64 * 1024;
    private static readonly nint WarmUpHandle = GCHandle.ToIntPtr(GCHandle.Alloc(new object()));

    private readonly string _module;
    private readonly Action<ModuleLoadTrace>? _captured;
    private readonly byte[] _stack = new byte[StackCopyBytes];
    private GCHandle _self;
    private nint _cookie;
    private int _matched;
    private int _disposed;
    private ModuleLoadTrace? _trace;
    private int _managedThreadId;
    private uint _osThreadId;
    private string? _threadName;
    private string? _managedStack;
    private int _stackBytes;

    private ModuleLoadTracer(string module, Action<ModuleLoadTrace>? captured)
    {
        _module = module;
        _captured = captured;
    }

    /// <summary>The captured load, once the module was loaded after <see cref="Start"/>.</summary>
    public ModuleLoadTrace? Trace => Volatile.Read(ref _trace);

    /// <summary>True while the loader notification is registered.</summary>
    public bool IsRegistered => Volatile.Read(ref _cookie) != 0;

    /// <summary>Starts tracing loads of <paramref name="module"/> (a base name such as d3d10warp.dll).</summary>
    public static ModuleLoadTracer Start(string module, Action<ModuleLoadTrace>? captured = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        var tracer = new ModuleLoadTracer(module, captured);
        WarmUp();
        tracer._self = GCHandle.Alloc(tracer);
        var status = LdrRegisterDllNotification(
            0, (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnNotification,
            GCHandle.ToIntPtr(tracer._self), out var cookie);
        if (status < 0) tracer._self.Free();
        else Volatile.Write(ref tracer._cookie, cookie);
        return tracer;
    }

    /// <summary>True for the loader's base name of <paramref name="module"/>, compared without allocating.</summary>
    internal static bool Matches(ReadOnlySpan<char> baseName, string module) =>
        baseName.Equals(module, StringComparison.OrdinalIgnoreCase);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnNotification(uint reason, nint data, nint context)
    {
        try
        {
            if (reason != LoadedReason || data == 0 || context == 0) return;
            if (GCHandle.FromIntPtr(context).Target is not ModuleLoadTracer tracer
                || Volatile.Read(ref tracer._matched) != 0) return;
            var name = ((LoadedNotificationData*)data)->BaseDllName;
            if (name is null || name->Buffer == 0) return;
            if (!Matches(new ReadOnlySpan<char>((void*)name->Buffer, name->Length / sizeof(char)), tracer._module)) return;
            tracer.Capture();
        }
        catch (Exception)
        {
            // Nothing may escape into the loader.
        }
    }

    private void Capture()
    {
        if (Interlocked.Exchange(ref _matched, 1) != 0) return;
        // Only raw values and one copy of the loading thread's stack here. A frame walk
        // (RtlCaptureStackBackTrace) stops at the first JIT frame, which is this callback, so the
        // callers are found off the loader lock by scanning the copy for return addresses.
        _managedThreadId = Environment.CurrentManagedThreadId;
        _osThreadId = GetCurrentThreadId();
        _threadName = Thread.CurrentThread.Name;
        _managedStack = new StackTrace(1, false).ToString();
        nint marker = 0;
        var stackPointer = (nuint)(&marker);
        GetCurrentThreadStackLimits(out _, out var stackHigh);
        var available = stackHigh > stackPointer ? stackHigh - stackPointer : 0;
        _stackBytes = (int)Math.Min((nuint)_stack.Length, available);
        new ReadOnlySpan<byte>((void*)stackPointer, _stackBytes).CopyTo(_stack);
        // A reference-type state shares the generic work-item code compiled by the warm-up.
        ThreadPool.UnsafeQueueUserWorkItem(static tracer => tracer.Complete(), this, preferLocal: false);
    }

    private void Complete()
    {
        try
        {
            var trace = new ModuleLoadTrace(
                _module,
                _managedThreadId,
                _osThreadId,
                string.IsNullOrEmpty(_threadName) ? "unnamed" : _threadName,
                DescribeNativeCallers(_stack.AsSpan(0, _stackBytes)),
                string.IsNullOrWhiteSpace(_managedStack) ? "no managed frames (native thread)" : _managedStack.Trim());
            Volatile.Write(ref _trace, trace);
            _captured?.Invoke(trace);
        }
        catch (Exception)
        {
            // A diagnostic must never fail the process.
        }
        finally
        {
            Array.Clear(_stack);
            Dispose();
        }
    }

    /// <summary>
    /// Modules whose code the copied stack returns into, each named once in the order the newest
    /// callers first appear: "ntdll.dll > dxgi.dll > KERNELBASE.dll > d3d11.dll > ...". A value
    /// counts only when it lies in a loaded image right after a call instruction, so stale stack
    /// data rarely shows; it is a scan, not an exact unwind.
    /// </summary>
    private static string DescribeNativeCallers(ReadOnlySpan<byte> stack)
    {
        var modules = LoadedModules();
        var listed = new List<string>();
        for (var offset = 0; offset + sizeof(ulong) <= stack.Length && listed.Count < MaximumNativeCallers; offset += sizeof(ulong))
        {
            var value = (nint)BitConverter.ToUInt64(stack.Slice(offset, sizeof(ulong)));
            if (FindModule(modules, value) is not { } module || !FollowsCall(value, module.Base)) continue;
            if (!listed.Contains(module.Name, StringComparer.OrdinalIgnoreCase)) listed.Add(module.Name);
        }
        return listed.Count == 0 ? "none found" : string.Join(" > ", listed);
    }

    private static (nint Base, nint End, string Name)[] LoadedModules()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>()
            .Select(module => (module.BaseAddress, module.BaseAddress + module.ModuleMemorySize, module.ModuleName))
            .OrderBy(module => module.BaseAddress)
            .ToArray();
    }

    private static (nint Base, string Name)? FindModule((nint Base, nint End, string Name)[] modules, nint address)
    {
        int low = 0, high = modules.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (address < modules[middle].Base) high = middle - 1;
            else if (address >= modules[middle].End) low = middle + 1;
            else return (modules[middle].Base, modules[middle].Name);
        }
        return null;
    }

    /// <summary>True when the bytes before <paramref name="address"/> are a call (x64) or BL/BLR (ARM64).</summary>
    private static bool FollowsCall(nint address, nint moduleBase)
    {
        if (address - 8 < moduleBase) return false;
        var before = new ReadOnlySpan<byte>((void*)(address - 8), 8);
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? IsArm64Call(BitConverter.ToUInt32(before[4..]))
            : IsX64Call(before);
    }

    /// <summary>The eight bytes before a return address end with a near call encoding.</summary>
    internal static bool IsX64Call(ReadOnlySpan<byte> before) =>
        before.Length >= 8
        && (before[^5] == 0xE8 // call rel32
            || (before[^6] == 0xFF && before[^5] == 0x15) // call [rip+disp32]
            || (before[^6] == 0xFF && (before[^5] & 0xF8) == 0x90) // call [reg+disp32]
            || (before[^3] == 0xFF && (before[^2] & 0xF8) == 0x50) // call [reg+disp8]
            || (before[^2] == 0xFF && (before[^1] & 0xF8) == 0xD0)); // call reg

    /// <summary>BL, BLR or a pointer-authenticated BLRAA/BLRAB form.</summary>
    internal static bool IsArm64Call(uint instruction) =>
        (instruction & 0xFC000000) == 0x94000000
        || (instruction & 0xFFFFFC1F) == 0xD63F0000
        || (instruction & 0xFEFFF800) == 0xD63F0800;

    /// <summary>JITs the matching path and creates its P/Invoke stubs before the loader can call it.</summary>
    private static void WarmUp()
    {
        RuntimeHelpers.RunClassConstructor(typeof(ModuleLoadTracer).TypeHandle);
        _ = Matches("warm-up.dll", "WARM-UP.DLL");
        _ = Environment.CurrentManagedThreadId;
        _ = GetCurrentThreadId();
        _ = Thread.CurrentThread.Name;
        _ = new StackTrace(1, false).ToString();
        GetCurrentThreadStackLimits(out _, out _);
        new ReadOnlySpan<byte>(new byte[8]).CopyTo(new byte[8]);
        _ = GCHandle.FromIntPtr(WarmUpHandle).Target;
        ThreadPool.UnsafeQueueUserWorkItem(static _ => { }, (object?)null, preferLocal: false);
        // Enter the callback once through its native entry point; reason 0 returns at once.
        ((delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnNotification)(0, 0, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var cookie = Interlocked.Exchange(ref _cookie, 0);
        if (cookie == 0) return;
        _ = LdrUnregisterDllNotification(cookie);
        // The loader no longer calls back with this context once unregistration returned.
        if (_self.IsAllocated) _self.Free();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LoadedNotificationData
    {
        public uint Flags;
        public UnicodeString* FullDllName;
        public UnicodeString* BaseDllName;
        public nint DllBase;
        public uint SizeOfImage;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int LdrRegisterDllNotification(uint flags, nint callback, nint context, out nint cookie);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int LdrUnregisterDllNotification(nint cookie);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void GetCurrentThreadStackLimits(out nuint lowLimit, out nuint highLimit);
}
