using System.Security.Cryptography;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// A private, bounded pool for full-frame presentation buffers of one size. A 4K BGRA frame is
/// 33 MB; allocating one per frame puts ~1 GB/s on the large-object heap and turns almost every
/// collection into a gen2 collection, which shows up as periodic stutter. Retained buffers are
/// cleared when the size changes and when the owning session closes.
/// </summary>
internal sealed class FrameBufferPool(int capacity = 3)
{
    private readonly Lock _gate = new();
    private readonly Stack<byte[]> _buffers = new();
    private int _length;
    private bool _closed;

    public int RetainedBuffers { get { lock (_gate) return _buffers.Count; } }

    public byte[] Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        lock (_gate)
        {
            if (length != _length)
            {
                ClearRetained();
                _length = length;
            }
            if (!_closed && _buffers.TryPop(out var buffer)) return buffer;
        }
        return GC.AllocateUninitializedArray<byte>(length);
    }

    /// <summary>Accepts a buffer whose previous owner will never touch it again.</summary>
    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_gate)
        {
            if (!_closed && buffer.Length == _length && _buffers.Count < capacity && !_buffers.Contains(buffer))
            {
                _buffers.Push(buffer);
                return;
            }
        }
        CryptographicOperations.ZeroMemory(buffer);
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            ClearRetained();
        }
    }

    private void ClearRetained()
    {
        while (_buffers.TryPop(out var retained)) CryptographicOperations.ZeroMemory(retained);
    }
}
