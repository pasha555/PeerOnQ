using PeerOnQ.Application.Abstractions;
using PeerOnQ.Platform.Apple;
using Xunit;

namespace PeerOnQ.Platform.Apple.Tests;

public sealed class AppleInputAndViewportTests
{
    [Theory]
    [InlineData('a', 0x41, false)]
    [InlineData('Z', 0x5a, true)]
    [InlineData('9', 0x39, false)]
    [InlineData('?', 0xbf, true)]
    [InlineData('\n', 0x0d, false)]
    public void AsciiMapper_UsesProtocolVirtualKeys(char character, ushort expected, bool shift)
    {
        Assert.True(AppleInputMapper.TryMapAscii(character, out var actual, out var actualShift));
        Assert.Equal(expected, actual);
        Assert.Equal(shift, actualShift);
    }

    [Fact]
    public void AsciiMapper_RejectsUnicodeThatCannotBeRepresentedSafely()
    {
        Assert.False(AppleInputMapper.TryMapAscii('\u0131', out _, out _));
    }

    [Fact]
    public void ViewportMapper_NormalizesAspectFitCoordinates()
    {
        Assert.True(RemoteViewportMapper.TryNormalize(
            pointerX: 500,
            pointerY: 250,
            viewportWidth: 1_000,
            viewportHeight: 500,
            videoWidth: 1_600,
            videoHeight: 900,
            allowOutside: false,
            out var x,
            out var y));

        Assert.Equal(0.5, x, 6);
        Assert.Equal(0.5, y, 6);
    }

    [Fact]
    public void ViewportMapper_RejectsLetterboxTouchesUnlessReleaseIsAllowed()
    {
        Assert.False(RemoteViewportMapper.TryNormalize(
            10, 10, 1_000, 1_000, 1_600, 900, false, out _, out _));
        Assert.True(RemoteViewportMapper.TryNormalize(
            10, 10, 1_000, 1_000, 1_600, 900, true, out var x, out var y));
        Assert.Equal(0.01, x, 6);
        Assert.Equal(0, y);
    }

    [Fact]
    public void FrameConverter_ConvertsBgrToOpaqueBgra()
    {
        var frame = new RemoteVideoFrame(
            2,
            1,
            new byte[] { 1, 2, 3, 10, 20, 30 },
            RemotePixelFormat.Bgr24,
            TimeSpan.Zero);

        Assert.True(AppleFramePixelConverter.TryConvertToBgra(frame, out var pixels));
        Assert.Equal(new byte[] { 1, 2, 3, 255, 10, 20, 30, 255 }, pixels);
    }

    [Fact]
    public void FrameConverter_RejectsOversizedFrameBeforeAllocation()
    {
        var frame = new RemoteVideoFrame(
            8_192,
            8_192,
            ReadOnlyMemory<byte>.Empty,
            RemotePixelFormat.Bgr24,
            TimeSpan.Zero);

        Assert.False(AppleFramePixelConverter.TryConvertToBgra(frame, out _));
    }
}
