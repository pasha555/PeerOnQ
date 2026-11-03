using PeerOnQ.Platform.Windows.Media;
using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class PreparedTargetH264EncoderTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly byte[] Frame = new byte[16 * 16 * 3 / 2];

    [Fact]
    public void A_new_target_keeps_the_current_mft_encoding_until_its_replacement_is_ready()
    {
        var factory = new FakeFactory();
        using var encoder = new PreparedTargetH264Encoder(factory.Create);
        factory.PrepareGate.Reset();
        encoder.EncodeI420(16, 16, Frame, 10_000, 60, false);

        encoder.EncodeI420(16, 16, Frame, 2_000, 20, false);
        encoder.EncodeI420(16, 16, Frame, 2_000, 20, false);

        // The 150-230 ms creation runs off the encode loop; frames keep the old target meanwhile.
        Assert.True(encoder.IsReplacementPending);
        Assert.Equal(new[] { (16, 16, 10_000, 60), (16, 16, 10_000, 60), (16, 16, 10_000, 60) }, factory[0].Encoded);
        Assert.Empty(factory[1].Encoded);

        factory.PrepareGate.Set();
        EncodeUntil(encoder, 16, 16, 2_000, 20, () => factory[1].Encoded.Count > 0);

        Assert.Equal((16, 16, 2_000, 20), factory[1].Prepared);
        Assert.Equal((16, 16, 2_000, 20), factory[1].Encoded[0]);
        Assert.Equal(factory[0].NextSampleTime, factory[1].FirstSampleTime);
        Assert.Equal(1, encoder.Switches);
        Assert.Equal(0, encoder.InPlaceFallbacks);
        Assert.True(SpinWait.SpinUntil(() => factory[0].Disposed, Patience), "The retired MFT was not released.");
    }

    [Fact]
    public void A_replacement_the_driver_refuses_falls_back_to_recreating_the_current_mft()
    {
        var factory = new FakeFactory { FailPrepareFrom = 1 };
        using var encoder = new PreparedTargetH264Encoder(factory.Create);
        encoder.EncodeI420(16, 16, Frame, 10_000, 60, false);

        EncodeUntil(encoder, 16, 16, 2_000, 20, () => factory[0].Encoded[^1] == (16, 16, 2_000, 20));

        Assert.Equal(1, encoder.InPlaceFallbacks);
        Assert.Equal(0, encoder.Switches);
        Assert.True(factory[1].Disposed);
        Assert.False(encoder.IsReplacementPending);
    }

    [Fact]
    public void Abandoned_or_resized_replacements_are_released_and_dispose_releases_everything()
    {
        var factory = new FakeFactory();
        var encoder = new PreparedTargetH264Encoder(factory.Create);
        factory.PrepareGate.Reset();
        encoder.EncodeI420(16, 16, Frame, 10_000, 60, false);
        encoder.EncodeI420(16, 16, Frame, 2_000, 20, false);

        // The rung came back before the replacement was ready: it is released, not switched to.
        encoder.EncodeI420(16, 16, Frame, 10_000, 60, false);
        Assert.False(encoder.IsReplacementPending);
        factory.PrepareGate.Set();
        Assert.True(SpinWait.SpinUntil(() => factory[1].Disposed, Patience));
        Assert.Empty(factory[1].Encoded);

        // A new size cannot wait for a replacement: the current MFT recreates in place.
        factory.PrepareGate.Reset();
        encoder.EncodeI420(16, 16, Frame, 2_000, 20, false);
        encoder.EncodeI420(32, 32, new byte[32 * 32 * 3 / 2], 2_000, 20, false);
        Assert.Equal((32, 32, 2_000, 20), factory[0].Encoded[^1]);
        factory.PrepareGate.Set();
        Assert.True(SpinWait.SpinUntil(() => factory[2].Disposed, Patience));

        factory.PrepareGate.Reset();
        encoder.EncodeI420(32, 32, new byte[32 * 32 * 3 / 2], 5_000, 30, false);
        encoder.Dispose();
        Assert.True(factory[0].Disposed);
        factory.PrepareGate.Set();
        Assert.True(SpinWait.SpinUntil(() => factory[3].Disposed, Patience));
        Assert.Throws<ObjectDisposedException>(() => encoder.EncodeI420(16, 16, Frame, 2_000, 20, false));
    }

    private static void EncodeUntil(PreparedTargetH264Encoder encoder, int width, int height, int kbps, int fps,
        Func<bool> done)
    {
        var frame = new byte[width * height * 3 / 2];
        var deadline = DateTime.UtcNow + Patience;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The prepared target was never applied.");
            encoder.EncodeI420(width, height, frame, kbps, fps, false);
            Thread.Sleep(1);
        }
    }

    private sealed class FakeFactory
    {
        private readonly Lock _gate = new();
        private readonly List<FakeEncoder> _created = [];

        public ManualResetEventSlim PrepareGate { get; } = new(true);
        public int FailPrepareFrom { get; init; } = int.MaxValue;

        public FakeEncoder this[int index]
        {
            get
            {
                Assert.True(SpinWait.SpinUntil(() => { lock (_gate) return _created.Count > index; }, Patience));
                lock (_gate) return _created[index];
            }
        }

        public IRetargetableH264Encoder Create()
        {
            lock (_gate)
            {
                var encoder = new FakeEncoder(this, _created.Count >= FailPrepareFrom);
                _created.Add(encoder);
                return encoder;
            }
        }

        public sealed class FakeEncoder(FakeFactory factory, bool failPrepare) : IRetargetableH264Encoder
        {
            public List<(int Width, int Height, int Kbps, int Fps)> Encoded { get; } = [];
            public (int Width, int Height, int Kbps, int Fps)? Prepared { get; private set; }
            public long? FirstSampleTime { get; private set; }
            public volatile bool Disposed;
            public long NextSampleTime { get; set; }
            public string Name => "fake";

            public void Prepare(int width, int height, int bitrateKbps, int frameRate)
            {
                Assert.True(factory.PrepareGate.Wait(Patience));
                if (failPrepare) throw new InvalidOperationException("No second hardware session.");
                Prepared = (width, height, bitrateKbps, frameRate);
            }

            public byte[] EncodeI420(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                FirstSampleTime ??= NextSampleTime;
                Encoded.Add((width, height, bitrateKbps, frameRate));
                NextSampleTime += 10_000_000L / frameRate;
                return [1];
            }

            public void Dispose() => Disposed = true;
        }
    }
}
