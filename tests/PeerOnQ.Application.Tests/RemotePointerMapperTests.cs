using PeerOnQ.Application.Collaboration;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class RemotePointerMapperTests
{
    [Theory]
    [InlineData(1.0, 3840, 2160)]
    [InlineData(1.25, 3072, 1728)]
    [InlineData(1.5, 2560, 1440)]
    [InlineData(2.0, 1920, 1080)]
    public void Actual_size_preserves_one_physical_pixel_per_source_pixel(
        double rasterizationScale,
        double expectedWidth,
        double expectedHeight)
    {
        var size = RemotePointerMapper.ActualSizeInDips(3840, 2160, rasterizationScale);

        Assert.Equal(expectedWidth, size.Width, precision: 6);
        Assert.Equal(expectedHeight, size.Height, precision: 6);
    }

    [Fact]
    public void Uniform_fit_maps_frame_center_and_rejects_letterbox_bars()
    {
        var content = RemotePointerMapper.UniformFit(
            new RemoteContentBounds(0, 0, 1920, 1200),
            frameWidth: 3840,
            frameHeight: 2160);

        Assert.Equal(new RemoteContentBounds(0, 60, 1920, 1080), content);
        Assert.True(RemotePointerMapper.TryNormalize(960, 600, content, out var x, out var y));
        Assert.Equal(0.5, x, precision: 6);
        Assert.Equal(0.5, y, precision: 6);
        Assert.False(RemotePointerMapper.TryNormalize(960, 30, content, out _, out _));
    }

    [Fact]
    public void Uniform_fill_maps_the_visible_cropped_source_region()
    {
        var content = RemotePointerMapper.UniformFill(
            new RemoteContentBounds(0, 0, 1920, 1200),
            frameWidth: 3840,
            frameHeight: 2160);

        Assert.Equal(-106.666667, content.X, precision: 5);
        Assert.Equal(0, content.Y, precision: 6);
        Assert.Equal(2133.333333, content.Width, precision: 5);
        Assert.Equal(1200, content.Height, precision: 6);
        Assert.True(RemotePointerMapper.TryNormalize(960, 600, content, out var centerX, out var centerY));
        Assert.Equal(0.5, centerX, precision: 6);
        Assert.Equal(0.5, centerY, precision: 6);
        Assert.True(RemotePointerMapper.TryNormalize(0, 600, content, out var leftX, out _));
        Assert.Equal(0.05, leftX, precision: 6);
    }

    [Fact]
    public void Real_transformed_bounds_preserve_actual_size_scroll_offsets()
    {
        var scrolledImage = new RemoteContentBounds(-640, -360, 3840, 2160);

        Assert.True(RemotePointerMapper.TryNormalize(1280, 720, scrolledImage, out var x, out var y));
        Assert.Equal(0.5, x, precision: 6);
        Assert.Equal(0.5, y, precision: 6);
    }

    [Fact]
    public void Exact_content_edges_map_to_protocol_edges()
    {
        var content = new RemoteContentBounds(100, 50, 800, 600);

        Assert.True(RemotePointerMapper.TryNormalize(100, 50, content, out var left, out var top));
        Assert.True(RemotePointerMapper.TryNormalize(900, 650, content, out var right, out var bottom));
        Assert.Equal(0, left);
        Assert.Equal(0, top);
        Assert.Equal(1, right);
        Assert.Equal(1, bottom);
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(10, double.PositiveInfinity)]
    public void Invalid_pointer_coordinates_are_rejected(double x, double y)
    {
        Assert.False(RemotePointerMapper.TryNormalize(
            x,
            y,
            new RemoteContentBounds(0, 0, 100, 100),
            out _,
            out _));
    }
}
