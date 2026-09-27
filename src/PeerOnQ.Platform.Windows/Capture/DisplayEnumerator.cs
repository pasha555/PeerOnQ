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

    public static IReadOnlyList<CaptureTargetInfo> ListCaptureTargets() =>
        ListDisplays()
            .OrderByDescending(d => d.IsPrimary)
            .Select(d => new CaptureTargetInfo(CaptureTargetKind.Display, d.DeviceName, d.DisplayName, d.Width, d.Height))
            .ToArray();

    public static DisplayTarget? FindByDeviceName(string deviceName) =>
        ListDisplays().FirstOrDefault(d => d.DeviceName == deviceName);

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
