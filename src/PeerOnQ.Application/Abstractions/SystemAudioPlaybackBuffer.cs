using System.Security.Cryptography;

namespace PeerOnQ.Application.Abstractions;

/// <summary>100ms hard bound, 40ms initial/underflow prebuffer, newest sound wins.</summary>
public sealed class SystemAudioPlaybackBuffer
{
    public const int CapacityBytes = 5 * SystemAudioFrameCodec.PcmBytes;
    public const int PrebufferBytes = 2 * SystemAudioFrameCodec.PcmBytes;
    private readonly Lock _gate = new();
    private readonly byte[] _buffer = new byte[CapacityBytes];
    private int _head;
    private int _count;
    private bool _playing;

    public int BufferedBytes { get { lock (_gate) return _count; } }

    public void Add(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length != SystemAudioFrameCodec.PcmBytes) throw new ArgumentException("Invalid audio packet.", nameof(pcm));
        lock (_gate)
        {
            var discard = Math.Max(0, _count + pcm.Length - CapacityBytes);
            _head = (_head + discard) % CapacityBytes;
            _count -= discard;
            var tail = (_head + _count) % CapacityBytes;
            var first = Math.Min(pcm.Length, CapacityBytes - tail);
            pcm[..first].CopyTo(_buffer.AsSpan(tail));
            pcm[first..].CopyTo(_buffer);
            _count += pcm.Length;
        }
    }

    public int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            destination.Clear();
            if (!_playing && _count < PrebufferBytes) return destination.Length;
            _playing = true;
            var length = Math.Min(destination.Length, _count);
            var first = Math.Min(length, CapacityBytes - _head);
            _buffer.AsSpan(_head, first).CopyTo(destination);
            _buffer.AsSpan(0, length - first).CopyTo(destination[first..]);
            CryptographicOperations.ZeroMemory(_buffer.AsSpan(_head, first));
            CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, length - first));
            _head = (_head + length) % CapacityBytes;
            _count -= length;
            if (length < destination.Length) _playing = false;
            return destination.Length;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            CryptographicOperations.ZeroMemory(_buffer);
            _head = _count = 0;
            _playing = false;
        }
    }
}
