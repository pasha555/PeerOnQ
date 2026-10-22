using PeerOnQ.Application.Abstractions;
using Xunit;

namespace PeerOnQ.App.Linux.Tests;

public sealed class FramePixelConverterTests
{
    [Fact]
    public void PackedBgrRowsWithSixPixelWidthRenderWithoutNativePadding()
    {
        var source = Enumerable.Range(0, 6 * 4 * 3).Select(value => (byte)value).ToArray();
        var frame = new RemoteVideoFrame(6, 4, source, RemotePixelFormat.Bgr24, TimeSpan.Zero);

        Assert.True(FramePixelConverter.TryConvertToBgra(frame, out var output));

        Assert.Equal(6 * 4 * 4, output.Length);
        for (var pixel = 0; pixel < 6 * 4; pixel++)
        {
            Assert.Equal(source.AsSpan(pixel * 3, 3).ToArray(), output.AsSpan(pixel * 4, 3).ToArray());
            Assert.Equal(255, output[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void PresentationPreservesTheExactDecodedFramesAuthenticatedTiming()
    {
        var frame = new RemoteVideoFrame(2, 2, new byte[12], RemotePixelFormat.Bgr24, TimeSpan.Zero)
        {
            PipelineDecodedTimestamp = 1234,
            CaptureTimestampUnixMicroseconds = 5678,
            CaptureSequenceNumber = 42,
            PeerClockEstimate = new PeerClockEstimate(300, 50),
        };

        var presented = FramePixelConverter.DescribePresentation(frame);

        Assert.Equal(new PresentedVideoFrame(1234, 2, 2, 5678, 42, new PeerClockEstimate(300, 50)), presented);
    }

    [Fact]
    public void Bgr24_IsConvertedToOpaqueBgra()
    {
        var frame = new RemoteVideoFrame(
            2,
            1,
            new byte[] { 1, 2, 3, 10, 20, 30 },
            RemotePixelFormat.Bgr24,
            TimeSpan.Zero);

        var converted = FramePixelConverter.TryConvertToBgra(frame, out var pixels);

        Assert.True(converted);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 10, 20, 30, 255 }, pixels);
    }

    [Fact]
    public void I420NeutralBlack_IsConvertedToOpaqueBlack()
    {
        var frame = new RemoteVideoFrame(
            2,
            2,
            new byte[] { 16, 16, 16, 16, 128, 128 },
            RemotePixelFormat.I420,
            TimeSpan.Zero);

        var converted = FramePixelConverter.TryConvertToBgra(frame, out var pixels);

        Assert.True(converted);
        Assert.Equal(
            new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 },
            pixels);
    }

    [Fact]
    public void InvalidBuffer_IsRejected()
    {
        var frame = new RemoteVideoFrame(2, 2, new byte[3], RemotePixelFormat.Bgr24, TimeSpan.Zero);

        Assert.False(FramePixelConverter.TryConvertToBgra(frame, out _));
    }

    [Fact]
    public void OversizedFrame_IsRejectedBeforeAllocation()
    {
        var frame = new RemoteVideoFrame(8_192, 8_192, ReadOnlyMemory<byte>.Empty, RemotePixelFormat.Bgr24, TimeSpan.Zero);

        Assert.False(FramePixelConverter.TryConvertToBgra(frame, out _));
    }
}
