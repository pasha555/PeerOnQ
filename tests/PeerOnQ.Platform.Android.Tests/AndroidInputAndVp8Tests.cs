using PeerOnQ.Platform.Android;
using Xunit;

namespace PeerOnQ.Platform.Android.Tests;

public sealed class AndroidInputAndVp8Tests
{
    [Fact]
    public void Vp8KeyFrameParser_ReadsCodedDimensions()
    {
        byte[] frame = [0x10, 0, 0, 0x9d, 0x01, 0x2a, 0x80, 0x02, 0x68, 0x01];

        Assert.True(Vp8KeyFrameParser.TryReadDimensions(frame, out var dimensions));
        Assert.Equal(640, dimensions.Width);
        Assert.Equal(360, dimensions.Height);
    }

    [Fact]
    public void Vp8KeyFrameParser_RejectsInterFramesAndInvalidDimensions()
    {
        Assert.False(Vp8KeyFrameParser.TryReadDimensions(
            [0x11, 0, 0, 0x9d, 0x01, 0x2a, 0x80, 0x02, 0x68, 0x01], out _));
        Assert.False(Vp8KeyFrameParser.TryReadDimensions(
            [0x10, 0, 0, 0x9d, 0x01, 0x2a, 0, 0, 0, 0], out _));
    }

    [Theory]
    [InlineData(29, 0x41, false)]
    [InlineData(54, 0x5a, false)]
    [InlineData(7, 0x30, false)]
    [InlineData(22, 0x27, true)]
    [InlineData(111, 0x1b, false)]
    public void KeyMapper_UsesProtocolVirtualKeys(int keyCode, ushort expected, bool extended)
    {
        Assert.True(AndroidKeyCodeMapper.TryMap(keyCode, out var actual, out var actualExtended));
        Assert.Equal(expected, actual);
        Assert.Equal(extended, actualExtended);
    }

    [Fact]
    public void KeyMapper_RejectsUnsupportedMediaKey()
    {
        Assert.False(AndroidKeyCodeMapper.TryMap(85, out _, out _));
    }
}
