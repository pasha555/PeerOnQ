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
                    isPrimary,
                    RefreshRateOf(name)));
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
            .Select(d => new CaptureTargetInfo(CaptureTargetKind.Display, d.DeviceName, d.DisplayName, d.Width, d.Height, d.X, d.Y, d.RefreshRateHz))
            .ToList();
        if (displays.Count > 1 && VirtualDesktop(displays) is { } all)
            targets.Add(new CaptureTargetInfo(CaptureTargetKind.Display, AllDisplaysId, AllDisplaysName, all.Width, all.Height, all.X, all.Y, all.RefreshRateHz));
        return targets;
    }

    /// <summary>
    /// The highest refresh rate among this device's displays (Hz), or null when none reports one.
    /// A viewer sends it with its requests so the sharer never streams more frames than it shows.
    /// </summary>
    public static int? MaximumRefreshRateHz()
    {
        var rates = ListDisplays().Select(display => display.RefreshRateHz).OfType<int>().ToArray();
        return rates.Length == 0 ? null : rates.Max();
    }

    /// <summary>Current refresh rate of a display device; null when Windows reports the hardware default.</summary>
    private static int? RefreshRateOf(string deviceName)
    {
        var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode)) return null;
        // 0 and 1 mean "the hardware's default rate"; anything else is the rate in Hz (59 = 59.94).
        return mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : null;
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
        // The composed view only refreshes as fast as its slowest display.
        var rates = displays.Select(d => d.RefreshRateHz).OfType<int>().ToArray();
        return new DisplayTarget(0, AllDisplaysId, AllDisplaysName, left, top, right - left, bottom - top, false,
            rates.Length == displays.Count ? rates.Min() : null);
    }

    private const int MonitorinfoPrimary = 0x00000001;
    private const int EnumCurrentSettings = -1;

    // DEVMODEW, display variant (the printer fields share the union and are not read here).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

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
    bool IsPrimary,
    int? RefreshRateHz = null);
