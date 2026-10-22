using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class SystemAudioSecurityTests
{
    [Fact]
    public void Receipts_are_bound_to_the_viewer_direction_audio_context_and_session_and_cannot_be_replayed()
    {
        var master = RandomNumberGenerator.GetBytes(64);
        var id = SessionId.New();
        using var viewer = new SessionTrafficProtector(master, id, SessionRole.Viewer);
        using var sharer = new SessionTrafficProtector(master, id, SessionRole.Sharer);
        using var reflected = new SessionTrafficProtector(master, id, SessionRole.Viewer);
        using var otherSession = new SessionTrafficProtector(master, SessionId.New(), SessionRole.Sharer);
        CryptographicOperations.ZeroMemory(master);
        var receipt = viewer.ProtectAudioFrame(SystemAudioReceiptCodec.Encode(7));
        var tampered = receipt.ToArray();
        tampered[^1] ^= 1;
        Assert.False(sharer.TryUnprotectAudioFrame(tampered, out _, out _));
        Assert.False(reflected.TryUnprotectAudioFrame(receipt, out _, out _));
        Assert.False(otherSession.TryUnprotectAudioFrame(receipt, out _, out _));
        Assert.False(sharer.TryUnprotect(SecureChannelKind.Input, Guid.Empty, receipt, out _, out _));
        Assert.True(sharer.TryUnprotectAudioFrame(receipt, out var body, out _));
        Assert.True(SystemAudioReceiptCodec.TryDecode(body, out var acknowledged));
        Assert.Equal(7, acknowledged);
        Assert.False(sharer.TryUnprotectAudioFrame(receipt, out _, out var error));
        Assert.Equal("stale_audio_record", error);
    }

    [Fact]
    public void Audio_authenticates_direction_session_context_and_payload_before_accepting_forward_gaps()
    {
        var master = RandomNumberGenerator.GetBytes(64);
        var id = SessionId.New();
        using var sender = new SessionTrafficProtector(master, id, SessionRole.Sharer);
        using var receiver = new SessionTrafficProtector(master, id, SessionRole.Viewer);
        using var wrongSession = new SessionTrafficProtector(master, SessionId.New(), SessionRole.Viewer);
        using var wrongDirection = new SessionTrafficProtector(master, id, SessionRole.Sharer);
        CryptographicOperations.ZeroMemory(master);
        _ = sender.ProtectAudioFrame("dropped"u8);
        var audio = sender.ProtectAudioFrame("pcm"u8);
        var tampered = audio.ToArray();
        tampered[^1] ^= 1;
        Assert.False(receiver.TryUnprotectAudioFrame(tampered, out _, out _));
        Assert.False(wrongSession.TryUnprotectAudioFrame(audio, out _, out _));
        Assert.False(wrongDirection.TryUnprotectAudioFrame(audio, out _, out _));
        Assert.False(receiver.TryUnprotectVideoFrame(audio, out _, out _));
        Assert.True(receiver.TryUnprotectAudioFrame(audio, out var plaintext, out var error), error);
        Assert.Equal("pcm"u8.ToArray(), plaintext);
        Assert.False(receiver.TryUnprotectAudioFrame(audio, out _, out var replayError));
        Assert.Equal("stale_audio_record", replayError);
        sender.ForceRekey();
        var nextEpoch = sender.ProtectAudioFrame("new"u8);
        Assert.True(receiver.TryUnprotectAudioFrame(nextEpoch, out _, out _));
        Assert.False(receiver.TryUnprotectAudioFrame(audio, out _, out var staleError));
        Assert.Equal("stale_audio_record", staleError);
    }

    [Theory]
    [InlineData(SecureChannelKind.Control)]
    [InlineData(SecureChannelKind.Input)]
    [InlineData(SecureChannelKind.Clipboard)]
    [InlineData(SecureChannelKind.Telemetry)]
    [InlineData(SecureChannelKind.FileTransfer)]
    public void Adding_lossy_audio_does_not_relax_other_ordered_secure_channels(SecureChannelKind channel)
    {
        var master = RandomNumberGenerator.GetBytes(64);
        var id = SessionId.New();
        using var sender = new SessionTrafficProtector(master, id, SessionRole.Sharer);
        using var receiver = new SessionTrafficProtector(master, id, SessionRole.Viewer);
        CryptographicOperations.ZeroMemory(master);
        var transferId = channel == SecureChannelKind.FileTransfer ? Guid.NewGuid() : Guid.Empty;
        var first = sender.Protect(channel, transferId, "first"u8);
        var second = sender.Protect(channel, transferId, "second"u8);
        Assert.False(receiver.TryUnprotect(channel, transferId, second, out _, out var error));
        Assert.Equal("invalid_initial_sequence", error);
        Assert.True(receiver.TryUnprotect(channel, transferId, first, out _, out _));
        Assert.True(receiver.TryUnprotect(channel, transferId, second, out _, out _));
    }
}
