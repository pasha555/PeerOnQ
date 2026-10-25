using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>
/// Windows.Graphics.Capture only emits a frame when something on screen changes. On an idle
/// test machine nothing moves, so tests that need a frame nudge the cursor by one pixel.
/// This changes the captured image (the cursor is included) without touching any window.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenActivity
{
    private static int _direction = 1;

    public static void Nudge()
    {
        if (!GetCursorPos(out var point)) return;

        SetCursorPos(point.X + _direction, point.Y);
        _direction = -_direction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
