using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Platform.Windows.Capture;

/// <summary>
/// Lists the displays the user can pick from. Capture always requires an explicit choice:
/// nothing here starts a capture, and there is no "capture everything" default.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class DisplayEnumerator
{
    public static IReadOnlyList<DisplayTarget> ListDisplays()
    {
        var displays = new List<DisplayTarget>();

        bool Callback(nint monitor, nint _, ref Rect rect, nint __)
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var isPrimary = (info.dwFlags & MonitorinfoPrimary) != 0;
                var width = info.rcMonitor.Right - info.rcMonitor.Left;
                var height = info.rcMonitor.Bottom - info.rcMonitor.Top;
                var name = info.szDevice.TrimEnd('\0');

                displays.Add(new DisplayTarget(
                    monitor,
                    name,
                    isPrimary ? $"{name} (primary)" : name,
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    width,
                    height,
                    isPrimary));
            }

            return true;
        }

        EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero);
        return displays;
    }

    /// <summary>Explicit target that composes every connected display into one view.</summary>
    public const string AllDisplaysId = "peeronq:all-displays";
    public const string AllDisplaysName = "All displays";

    public static bool IsAllDisplays(string? id) => string.Equals(id, AllDisplaysId, StringComparison.Ordinal);

    /// <summary>
    /// Individual displays, primary first, then an explicit "All displays" choice when more than
    /// one display exists. Capture still requires that explicit choice.
    /// </summary>
    public static IReadOnlyList<CaptureTargetInfo> ListCaptureTargets()
    {
        var displays = ListDisplays();
        var targets = displays
            .OrderByDescending(d => d.IsPrimary)
            .Select(d => new CaptureTargetInfo(CaptureTargetKind.Display, d.DeviceName, d.DisplayName, d.Width, d.Height))
            .ToList();
        if (displays.Count > 1 && VirtualDesktop(displays) is { } all)
            targets.Add(new CaptureTargetInfo(CaptureTargetKind.Display, AllDisplaysId, AllDisplaysName, all.Width, all.Height));
        return targets;
    }

    /// <summary>
    /// Finds a display, or the virtual-desktop bounds for <see cref="AllDisplaysId"/>, so remote
    /// pointer coordinates map across the composed view exactly like a single display.
    /// </summary>
    public static DisplayTarget? FindByDeviceName(string deviceName)
    {
        var displays = ListDisplays();
        return IsAllDisplays(deviceName)
            ? displays.Count > 1 ? VirtualDesktop(displays) : null
            : displays.FirstOrDefault(d => d.DeviceName == deviceName);
    }

    /// <summary>Bounding rectangle of the given displays in virtual-desktop pixels.</summary>
    public static DisplayTarget? VirtualDesktop(IReadOnlyList<DisplayTarget> displays)
    {
        if (displays.Count == 0) return null;
        var left = displays.Min(d => d.X);
        var top = displays.Min(d => d.Y);
        var right = displays.Max(d => d.X + d.Width);
        var bottom = displays.Max(d => d.Y + d.Height);
        return new DisplayTarget(0, AllDisplaysId, AllDisplaysName, left, top, right - left, bottom - top, false);
    }

    private const int MonitorinfoPrimary = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint dc, ref Rect rect, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
}

public sealed record DisplayTarget(
    nint MonitorHandle,
    string DeviceName,
    string DisplayName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary);
