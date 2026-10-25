using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class RemoteMonitorLayoutTests
{
    // A 1920x1080 primary with a 1280x1024 display to its left, 200 px lower: the virtual desktop
    // starts at (-1280, 0) and is 3200x1224.
    private static readonly CaptureTargetInfo Primary = Display("primary", 1920, 1080, 0, 0);
    private static readonly CaptureTargetInfo Left = Display("left", 1280, 1024, -1280, 200);
    private static readonly CaptureTargetInfo All = Display("all", 3200, 1224, -1280, 0);

    [Fact]
    public void Finds_the_combined_entry_and_keeps_the_sharer_order_of_the_displays()
    {
        var layout = RemoteMonitorLayout.TryCreate([Primary, Left, All]);

        Assert.NotNull(layout);
        Assert.Equal("all", layout.Combined.Id);
        Assert.Equal(["primary", "left"], layout.Monitors.Select(display => display.Id));
    }

    [Fact]
    public void Crops_each_display_out_of_a_downscaled_combined_frame()
    {
        var layout = RemoteMonitorLayout.TryCreate([Primary, Left, All])!;

        // The combined view arrives at half scale.
        Assert.Equal(new RemoteFrameCrop(640, 0, 960, 540), layout.CropFor(0, 1600, 612));
        Assert.Equal(new RemoteFrameCrop(0, 100, 640, 512), layout.CropFor(1, 1600, 612));
    }

    [Fact]
    public void Maps_a_point_of_one_display_to_the_combined_view_the_sharer_injects_into()
    {
        var layout = RemoteMonitorLayout.TryCreate([Primary, Left, All])!;

        var (centerX, centerY) = layout.ToCombined(0, 0.5, 0.5);
        Assert.Equal((1280 + 960) / 3200.0, centerX, precision: 9);
        Assert.Equal(540 / 1224.0, centerY, precision: 9);

        var (cornerX, cornerY) = layout.ToCombined(1, 1, 1);
        Assert.Equal(1280 / 3200.0, cornerX, precision: 9);
        Assert.Equal(1224 / 1224.0, cornerY, precision: 9);
    }

    [Fact]
    public void No_layout_without_positions_a_combined_entry_or_a_second_display()
    {
        // Older sharers publish sizes only.
        Assert.Null(RemoteMonitorLayout.TryCreate(
        [
            Primary with { X = null, Y = null },
            Left with { X = null, Y = null },
            All with { X = null, Y = null },
        ]));
        Assert.Null(RemoteMonitorLayout.TryCreate([Primary, Left]));
        Assert.Null(RemoteMonitorLayout.TryCreate([Primary, All with { Width = 1920, Height = 1080, X = 0 }]));
        // A combined entry that does not cover the displays exactly is not trusted.
        Assert.Null(RemoteMonitorLayout.TryCreate([Primary, Left, All with { Width = 3199 }]));
        Assert.Null(RemoteMonitorLayout.TryCreate([Primary, Left with { X = int.MinValue }, All]));
    }

    private static CaptureTargetInfo Display(string id, int width, int height, int x, int y) =>
        new(CaptureTargetKind.Display, id, id, width, height, x, y);
}
