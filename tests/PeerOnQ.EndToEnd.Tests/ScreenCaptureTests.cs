using PeerOnQ.Application.Abstractions;
using PeerOnQ.Platform.Windows.Capture;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>
/// Exercises the real Windows.Graphics.Capture path. These tests need an interactive desktop
/// session; on a session-less agent they report the reason instead of pretending to pass.
/// </summary>
[Collection(WindowsGraphicsCaptureCollection.Name)]
public class ScreenCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void Displays_can_be_enumerated()
    {
        var displays = DisplayEnumerator.ListDisplays();

        foreach (var display in displays)
        {
            output.WriteLine($"{display.DeviceName} {display.Width}x{display.Height} primary={display.IsPrimary}");
        }

        Assert.NotEmpty(displays);
        Assert.Contains(displays, d => d.IsPrimary);
        Assert.All(displays, d =>
        {
            Assert.True(d.Width > 0);
            Assert.True(d.Height > 0);
            Assert.NotEqual(nint.Zero, d.MonitorHandle);
        });
    }

    [Fact]
    public void Capture_targets_are_exposed_for_explicit_selection()
    {
        var targets = DisplayEnumerator.ListCaptureTargets();

        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.Equal(CaptureTargetKind.Display, t.Kind));
    }

    [Fact]
    public void The_platform_reports_whether_capture_is_supported()
    {
        output.WriteLine($"GraphicsCaptureSession.IsSupported = {WindowsGraphicsCaptureSource.IsSupported}");
        Assert.True(WindowsGraphicsCaptureSource.IsSupported, "Windows.Graphics.Capture is unavailable on this host.");
    }

    [Fact]
    public async Task Capturing_a_display_produces_real_I420_frames()
    {
        Assert.True(WindowsGraphicsCaptureSource.IsSupported);

        var target = DisplayEnumerator.ListCaptureTargets()[0];
        output.WriteLine($"Capturing {target.DisplayName} ({target.Width}x{target.Height})");

        await using var capture = new WindowsGraphicsCaptureSource();

        var frames = 0;
        CapturedFrame? sample = null;
        capture.FrameArrived += (_, frame) =>
        {
            Interlocked.Increment(ref frames);
            sample ??= frame;
        };

        await capture.StartAsync(new CaptureRequest
        {
            Target = target,
            IncludeCursor = true,
            MaxFramesPerSecond = 30,
        });

        Assert.True(capture.IsCapturing);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (frames < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await capture.StopAsync();

        output.WriteLine($"frames captured: {frames}");
        Assert.True(frames > 0, "No frames arrived from Windows.Graphics.Capture.");
        Assert.NotNull(sample);

        // I420 is Y plus two quarter-size chroma planes.
        var expected = sample!.Width * sample.Height * 3 / 2;
        Assert.Equal(expected, sample.I420.Length);
        Assert.Equal(0, sample.Width % 2);
        Assert.Equal(0, sample.Height % 2);
        Assert.True(sample.SequenceNumber >= 1);
        Assert.False(capture.IsCapturing);
    }

    [Fact]
    public async Task Stopping_and_disposing_is_idempotent()
    {
        await using var capture = new WindowsGraphicsCaptureSource();

        await capture.StopAsync();
        await capture.StopAsync();
        await capture.DisposeAsync();
        await capture.DisposeAsync();

        Assert.False(capture.IsCapturing);
    }

    [Fact]
    public async Task Capturing_an_unknown_display_fails_loudly()
    {
        await using var capture = new WindowsGraphicsCaptureSource();

        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StartAsync(new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Display, @"\\.\DISPLAY_NOPE", "Ghost", 1920, 1080),
        }));
    }

    [Fact]
    public async Task Window_capture_is_refused_when_only_full_display_is_supported()
    {
        await using var capture = new WindowsGraphicsCaptureSource();

        await Assert.ThrowsAsync<NotSupportedException>(() => capture.StartAsync(new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Window, "0x1234", "Some window", 800, 600),
        }));
    }

    [Fact]
    public async Task Repeated_capture_sessions_do_not_leak()
    {
        Assert.True(WindowsGraphicsCaptureSource.IsSupported);
        var target = DisplayEnumerator.ListCaptureTargets()[0];

        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < 5; i++)
        {
            await using var capture = new WindowsGraphicsCaptureSource();
            await capture.StartAsync(new CaptureRequest { Target = target, MaxFramesPerSecond = 15 });
            await Task.Delay(300);
            await capture.StopAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var after = GC.GetTotalMemory(forceFullCollection: true);
        var growthMb = (after - before) / (1024.0 * 1024.0);

        output.WriteLine($"managed heap growth after 5 capture sessions: {growthMb:0.00} MB");
        Assert.True(growthMb < 64, $"Managed heap grew by {growthMb:0.00} MB across repeated sessions.");
    }
}
