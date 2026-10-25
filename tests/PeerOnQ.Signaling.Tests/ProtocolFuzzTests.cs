using System.Text;
using PeerOnQ.Transport.Protocol;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class ProtocolFuzzTests
{
    [Fact]
    public void Random_and_structurally_hostile_frames_fail_closed_without_escaping_the_codec()
    {
        var random = new Random(0x4C494E4B);
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var bytes = new byte[random.Next(0, SignalingCodec.MaxFrameBytes + 1025)];
            random.NextBytes(bytes);

            var exception = Record.Exception(() => SignalingCodec.TryDecode(bytes, out _, out _));

            Assert.Null(exception);
        }

        var deeplyNested = Encoding.UTF8.GetBytes(new string('[', 256) + "0" + new string(']', 256));
        Assert.False(SignalingCodec.TryDecode(deeplyNested, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
