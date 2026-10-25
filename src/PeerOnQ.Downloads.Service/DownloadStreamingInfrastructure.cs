using Microsoft.Extensions.Options;
using PeerOnQ.Observability;

namespace PeerOnQ.Downloads.Service;

public sealed class DownloadStreamGate
{
    private readonly SemaphoreSlim _slots;

    public DownloadStreamGate(IOptions<DownloadsOptions> options)
    {
        var maximumStreams = options.Value.MaximumConcurrentStreams;
        _slots = new SemaphoreSlim(maximumStreams, maximumStreams);
    }

    public async ValueTask<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _slots.WaitAsync(TimeSpan.Zero, cancellationToken)) return null;
        return new GateLease(_slots);
    }

    private sealed class GateLease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}

public sealed class VerifiedArtifactCache
{
    private const string ArtifactExtension = ".artifact";
    private readonly DownloadsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _root;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FillLockState> _fillLocks = new(StringComparer.Ordinal);
    private long _usedBytes;
    private long _reservedBytes;

    public VerifiedArtifactCache(IOptions<DownloadsOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _root = Path.GetFullPath(_options.CacheDirectory);
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The dedicated download cache root must not be a reparse point.");
        LoadExistingEntries();
    }

    public async Task<VerifiedArtifactLease> OpenOrFillAsync(
        string sha256,
        long expectedSize,
        Func<string, CancellationToken, Task> fillAsync,
        CancellationToken cancellationToken)
    {
        var digest = NormalizeDigest(sha256);
        using var fillLease = await AcquireFillLockAsync(digest, cancellationToken);
        var existing = await TryOpenExistingAsync(digest, expectedSize, cancellationToken);
        if (existing is not null) return existing;

        Reserve(expectedSize);
        var partialPath = Path.Combine(_root, $"{digest}.{Guid.NewGuid():N}.partial");
        var finalPath = ArtifactPath(digest);
        var committed = false;
        try
        {
            await fillAsync(partialPath, cancellationToken);
            var partial = new FileInfo(partialPath);
            if (!partial.Exists || partial.Length != expectedSize)
                throw new IOException("The verified cache fill has an unexpected size.");

            File.Move(partialPath, finalPath, overwrite: false);
            committed = true;
            MakeReadOnly(finalPath);
            lock (_sync)
            {
                _reservedBytes -= expectedSize;
                _usedBytes += expectedSize;
                _entries[digest] = new CacheEntry(finalPath, expectedSize, verified: true, _timeProvider.GetUtcNow());
            }
        }
        catch
        {
            DeleteFile(partialPath);
            if (committed) DeleteFile(finalPath);
            lock (_sync) _reservedBytes -= expectedSize;
            throw;
        }

        return OpenKnown(digest);
    }

    private async Task<VerifiedArtifactLease?> TryOpenExistingAsync(
        string digest,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        CacheEntry? entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(digest, out entry)) return null;
            if (entry.Size != expectedSize)
            {
                RemoveEntry(entry, digest);
                return null;
            }

            if (entry.Verified) return OpenKnownLocked(entry);
            entry.ActiveReaders++;
        }

        bool verified;
        try
        {
            var actualDigest = await ComputeSha256Async(entry.Path, expectedSize, cancellationToken);
            verified = string.Equals(actualDigest, digest, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            verified = false;
        }
        catch
        {
            lock (_sync) entry.ActiveReaders--;
            throw;
        }

        lock (_sync)
        {
            entry.ActiveReaders--;
            if (!verified)
            {
                RemoveEntry(entry, digest);
                return null;
            }

            entry.Verified = true;
            return OpenKnownLocked(entry);
        }
    }

    private VerifiedArtifactLease OpenKnown(string digest)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(digest, out var entry) || !entry.Verified)
                throw new IOException("The verified artifact cache entry is unavailable.");
            return OpenKnownLocked(entry);
        }
    }

    private VerifiedArtifactLease OpenKnownLocked(CacheEntry entry)
    {
        var attributes = File.GetAttributes(entry.Path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The verified artifact cache entry is unsafe.");
        var stream = new FileStream(
            entry.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            _options.StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length != entry.Size)
        {
            stream.Dispose();
            throw new IOException("The verified artifact cache entry changed unexpectedly.");
        }

        entry.ActiveReaders++;
        entry.LastUsedUtc = _timeProvider.GetUtcNow();
        return new VerifiedArtifactLease(stream, () => ReleaseReader(entry));
    }

    private void ReleaseReader(CacheEntry entry)
    {
        lock (_sync)
        {
            entry.ActiveReaders--;
            entry.LastUsedUtc = _timeProvider.GetUtcNow();
        }
    }

    private void Reserve(long requiredBytes)
    {
        lock (_sync)
        {
            while (_usedBytes + _reservedBytes + requiredBytes > _options.MaximumCacheBytes)
            {
                var candidate = _entries
                    .Where(item => item.Value.ActiveReaders == 0)
                    .OrderBy(item => item.Value.LastUsedUtc)
                    .FirstOrDefault();
                if (candidate.Value is null)
                {
                    throw new ApiProblemException(
                        StatusCodes.Status503ServiceUnavailable,
                        "download_cache_capacity_exceeded",
                        "Verified download cache capacity is temporarily exhausted.");
                }
                RemoveEntry(candidate.Value, candidate.Key);
            }
            _reservedBytes += requiredBytes;
        }
    }

    private async ValueTask<IDisposable> AcquireFillLockAsync(string digest, CancellationToken cancellationToken)
    {
        FillLockState state;
        lock (_sync)
        {
            if (!_fillLocks.TryGetValue(digest, out state!))
            {
                state = new FillLockState();
                _fillLocks.Add(digest, state);
            }
            state.Users++;
        }

        try
        {
            await state.Semaphore.WaitAsync(cancellationToken);
            return new FillLockLease(this, digest, state);
        }
        catch
        {
            ReleaseFillLockReference(digest, state, acquired: false);
            throw;
        }
    }

    private void ReleaseFillLockReference(string digest, FillLockState state, bool acquired)
    {
        if (acquired) state.Semaphore.Release();
        lock (_sync)
        {
            state.Users--;
            if (state.Users == 0) _fillLocks.Remove(digest);
        }
    }

    private void LoadExistingEntries()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidOperationException("The dedicated download cache contains an unsafe entry.");

            if (name.EndsWith(".partial", StringComparison.Ordinal))
            {
                DeleteFile(path);
                continue;
            }

            if (!name.EndsWith(ArtifactExtension, StringComparison.Ordinal)
                || !TryNormalizeDigest(name[..^ArtifactExtension.Length], out var digest))
            {
                throw new InvalidOperationException("The dedicated download cache contains an unexpected entry.");
            }

            var info = new FileInfo(path);
            _entries.Add(digest, new CacheEntry(path, info.Length, verified: false, info.LastWriteTimeUtc));
            _usedBytes = checked(_usedBytes + info.Length);
        }

        lock (_sync)
        {
            while (_usedBytes > _options.MaximumCacheBytes)
            {
                var oldest = _entries.OrderBy(item => item.Value.LastUsedUtc).First();
                RemoveEntry(oldest.Value, oldest.Key);
            }
        }
    }

    private void RemoveEntry(CacheEntry entry, string digest)
    {
        DeleteFile(entry.Path);
        _entries.Remove(digest);
        _usedBytes -= entry.Size;
    }

    private static async Task<string> ComputeSha256Async(string path, long expectedSize, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedSize) return string.Empty;
        var digest = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private string ArtifactPath(string digest) => Path.Combine(_root, digest + ArtifactExtension);

    private static string NormalizeDigest(string value) =>
        TryNormalizeDigest(value, out var normalized)
            ? normalized
            : throw new ArgumentException("The artifact digest is invalid.", nameof(value));

    private static bool TryNormalizeDigest(string value, out string normalized)
    {
        normalized = value.ToLowerInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }

    private static void MakeReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (FileNotFoundException) { }
    }

    private sealed class CacheEntry(
        string path,
        long size,
        bool verified,
        DateTimeOffset lastUsedUtc)
    {
        public string Path { get; } = path;
        public long Size { get; } = size;
        public bool Verified { get; set; } = verified;
        public DateTimeOffset LastUsedUtc { get; set; } = lastUsedUtc;
        public int ActiveReaders { get; set; }
    }

    private sealed class FillLockState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class FillLockLease(
        VerifiedArtifactCache owner,
        string digest,
        FillLockState state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseFillLockReference(digest, state, acquired: true);
        }
    }
}

public sealed class VerifiedArtifactLease(
    FileStream stream,
    Action releaseReader) : IAsyncDisposable
{
    private int _disposed;

    public FileStream Stream { get; } = stream;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await Stream.DisposeAsync();
        }
        finally
        {
            releaseReader();
        }
    }
}
