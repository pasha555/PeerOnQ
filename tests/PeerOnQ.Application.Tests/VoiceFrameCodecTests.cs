using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class VoiceFrameCodecTests
{
    [Fact]
    public void Voice_frames_round_trip_with_bounded_fields()
    {
        var opus = RandomNumberGenerator.GetBytes(60);
        var encoded = VoiceFrameCodec.Encode(7, 1_700_000_000_000_000, opus);

        Assert.True(VoiceFrameCodec.TryDecode(encoded, out var sequence, out var capturedAt, out var payload));
        Assert.Equal(7, sequence);
        Assert.Equal(1_700_000_000_000_000, capturedAt);
        Assert.Equal(opus, payload.ToArray());
    }

    [Fact]
    public void Malformed_or_oversized_voice_frames_are_rejected()
    {
        var valid = VoiceFrameCodec.Encode(1, 1, [1, 2, 3]);
        Assert.False(VoiceFrameCodec.TryDecode(valid.AsSpan(0, VoiceFrameCodec.HeaderBytes), out _, out _, out _));
        var wrongMagic = (byte[])valid.Clone();
        wrongMagic[0] = (byte)'X';
        Assert.False(VoiceFrameCodec.TryDecode(wrongMagic, out _, out _, out _));
        var reserved = (byte[])valid.Clone();
        reserved[6] = 1;
        Assert.False(VoiceFrameCodec.TryDecode(reserved, out _, out _, out _));
        Assert.False(VoiceFrameCodec.TryDecode(new byte[VoiceFrameCodec.MaximumBytes + 1], out _, out _, out _));
        Assert.Throws<ArgumentException>(() => VoiceFrameCodec.Encode(1, 1, new byte[VoiceAudioFormat.MaximumEncodedBytes + 1]));
        Assert.Throws<ArgumentException>(() => VoiceFrameCodec.Encode(0, 1, [1]));
    }

    [Fact]
    public void Voice_records_use_their_own_direction_keys_and_tolerate_loss_but_not_replay()
    {
        var master = RandomNumberGenerator.GetBytes(64);
        var sessionId = SessionId.New();
        using var viewer = new SessionTrafficProtector(master, sessionId, SessionRole.Viewer);
        using var sharer = new SessionTrafficProtector(master, sessionId, SessionRole.Sharer);
        CryptographicOperations.ZeroMemory(master);

        var first = viewer.ProtectVoiceFrame("first"u8);
        var second = viewer.ProtectVoiceFrame("second"u8);
        var third = viewer.ProtectVoiceFrame("third"u8);

        // A lost record is fine for voice; a replayed or older one is rejected as stale.
        Assert.True(sharer.TryUnprotectVoiceFrame(third, out var plaintext, out _));
        Assert.Equal("third"u8.ToArray(), plaintext);
        Assert.False(sharer.TryUnprotectVoiceFrame(second, out _, out var stale));
        Assert.Equal("stale_voice_record", stale);
        Assert.False(sharer.TryUnprotectVoiceFrame(third, out _, out _));

        // Voice records cannot be read as system audio or video, and never in the sender's direction.
        Assert.False(sharer.TryUnprotectAudioFrame(first, out _, out _));
        Assert.False(viewer.TryUnprotectVoiceFrame(viewer.ProtectVoiceFrame("own"u8), out _, out _));
    }
}
