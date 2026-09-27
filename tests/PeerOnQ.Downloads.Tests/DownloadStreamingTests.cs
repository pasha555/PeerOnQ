using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Downloads.Service;
using PeerOnQ.Observability;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Downloads.Tests;

public sealed class DownloadStreamingTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public async Task Stream_LargerThanTmpfs_FillsDedicatedVerifiedCacheAndStreams()
    {
        const long size = 65 * MiB + 1;
        using var cache = new TemporaryCacheDirectory();
        var sha256 = PatternSha256(size);
        var tracking = new TrackingService(Artifact(size, sha256));
        using var metrics = new PeerOnQMetrics(new PeerOnQMetricState());
        var origin = new PatternOriginHandler(size);
        var service = Service(tracking, origin, metrics, cache.Path, maximumArtifactBytes: 128 * MiB);
        var context = Context(new HashingWriteStream());

        await StreamAsync(service, context);

        var response = Assert.IsType<HashingWriteStream>(context.Response.Body);
        Assert.Equal(size, response.BytesWritten);
        Assert.Equal(sha256, response.Sha256);
        Assert.Single(origin.Requests);
        Assert.Single(cache.Artifacts);
        Assert.Empty(cache.Partials);
        Assert.Equal(DownloadResultV1.Completed, Assert.Single(tracking.Completions).Result);
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
    }

    [Fact]
    public async Task Stream_CacheHitRange_DoesNotReadOriginAgain()
    {
        const long size = 512 * 1024;
        using var cache = new TemporaryCacheDirectory();
        var tracking = new TrackingService(Artifact(size, PatternSha256(size)));
        using var metrics = new PeerOnQMetrics(new PeerOnQMetricState());
        var origin = new PatternOriginHandler(size);
        var service = Service(tracking, origin, metrics, cache.Path);
        await StreamAsync(service, Context(new HashingWriteStream()));
        var rangeContext = Context();
        rangeContext.Request.Headers.Range = "bytes=1024-4095";

        await StreamAsync(service, rangeContext);

        Assert.Equal(StatusCodes.Status206PartialContent, rangeContext.Response.StatusCode);
        Assert.Equal(3072, rangeContext.Response.ContentLength);
        Assert.Equal($"bytes 1024-4095/{size}", rangeContext.Response.Headers.ContentRange);
        Assert.Equal(PatternBytes(1024, 3072), ((MemoryStream)rangeContext.Response.Body).ToArray());
        Assert.Single(origin.Requests);
        Assert.Equal(2, tracking.Completions.Count);
        Assert.Contains(tracking.Completions, completion => completion.Result == DownloadResultV1.Partial);
    }

    [Fact]
    public async Task Stream_MaliciousOrigin_WritesNoResponseAndLeavesNoCacheFile()
    {
        const long size = 32 * 1024;
        using var cache = new TemporaryCacheDirectory();
        var tracking = new TrackingService(Artifact(size, PatternSha256(size)));
        using var metrics = new PeerOnQMetrics(new PeerOnQMetricState());
        var origin = new PatternOriginHandler(size) { TamperContent = true };
        var service = Service(tracking, origin, metrics, cache.Path);
        var context = Context();

        var exception = await Assert.ThrowsAsync<CloudServiceException>(() => StreamAsync(service, context));

        Assert.Equal("ARTIFACT_INTEGRITY_MISMATCH", exception.Code);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
        Assert.False(context.Response.HasStarted);
        Assert.Empty(cache.Artifacts);
        Assert.Empty(cache.Partials);
        Assert.Equal(DownloadResultV1.Failed, Assert.Single(tracking.Completions).Result);
    }

    [Fact]
    public async Task Stream_Cancellation_RemovesPartialAndReleasesCapacity()
    {
        const long size = 32 * 1024;
        using var cache = new TemporaryCacheDirectory();
        var tracking = new TrackingService(Artifact(size, PatternSha256(size)));
        using var metrics = new PeerOnQMetrics(new PeerOnQMetricState());
        var origin = new InterruptibleOriginHandler(size);
        var service = Service(tracking, origin, metrics, cache.Path, maximumConcurrentStreams: 1);
        var context = Context();
        using var cancellation = new CancellationTokenSource();

        var attempt = StreamAsync(service, context, cancellation.Token);
        await origin.FirstChunkWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(cache.Partials);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
        Assert.Empty(cache.Artifacts);
        Assert.Empty(cache.Partials);
        Assert.Equal(DownloadResultV1.Cancelled, tracking.Completions.Single().Result);

        var retryContext = Context();
        await StreamAsync(service, retryContext);
        Assert.Equal(size, retryContext.Response.ContentLength);
        Assert.Single(cache.Artifacts);
    }

    [Fact]
    public async Task Cache_QuotaFailsClosedWhileOnlyEntryIsActiveThenEvictsLru()
    {
        using var cacheDirectory = new TemporaryCacheDirectory();
        var values = Settings(cacheDirectory.Path);
        values.MaximumCacheBytes = 64 * 1024;
        var cache = new VerifiedArtifactCache(Options.Create(values), TimeProvider.System);
        var firstDigest = new string('a', 64);
        var secondDigest = new string('b', 64);
        await using var first = await cache.OpenOrFillAsync(
            firstDigest,
            48 * 1024,
            static (path, cancellationToken) => WriteCacheFileAsync(path, 48 * 1024, cancellationToken),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ApiProblemException>(() => cache.OpenOrFillAsync(
            secondDigest,
            48 * 1024,
            static (path, cancellationToken) => WriteCacheFileAsync(path, 48 * 1024, cancellationToken),
            CancellationToken.None));
        Assert.Equal("download_cache_capacity_exceeded", exception.ErrorCode);

        await first.DisposeAsync();
        await using var second = await cache.OpenOrFillAsync(
            secondDigest,
            48 * 1024,
            static (path, cancellationToken) => WriteCacheFileAsync(path, 48 * 1024, cancellationToken),
            CancellationToken.None);
        Assert.Single(cacheDirectory.Artifacts);
        Assert.EndsWith(secondDigest + ".artifact", cacheDirectory.Artifacts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_ConcurrentCacheMiss_FillsOriginOnlyOnce()
    {
        const long size = 32 * 1024;
        using var cache = new TemporaryCacheDirectory();
        var tracking = new TrackingService(Artifact(size, PatternSha256(size)));
        using var metrics = new PeerOnQMetrics(new PeerOnQMetricState());
        var origin = new BlockingOriginHandler(size);
        var service = Service(tracking, origin, metrics, cache.Path, maximumConcurrentStreams: 2);
        var first = StreamAsync(service, Context());
        await origin.FirstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = StreamAsync(service, Context());
        await tracking.TwoStarts.Task.WaitAsync(TimeSpan.FromSeconds(5));

        origin.ReleaseFirstRequest.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, origin.RequestCount);
        Assert.Single(cache.Artifacts);
        Assert.Equal(2, tracking.Completions.Count);
    }

    [Fact]
    public async Task Stream_GlobalConcurrencyLimit_RejectsWithoutQueueingOrTracking()
    {
        const long size = 32 * 1024;
        using var cache = new TemporaryCacheDirectory();
        var tracking = new TrackingService(Artifact(size, PatternSha256(size)));
        using var metrics = new PeerOnQMetrics(new PeerOnQMetricState());
        var origin = new BlockingOriginHandler(size);
        var service = Service(tracking, origin, metrics, cache.Path, maximumConcurrentStreams: 1);
        var first = StreamAsync(service, Context());
        await origin.FirstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var rejectedContext = Context();
        var exception = await Assert.ThrowsAsync<ApiProblemException>(() => StreamAsync(service, rejectedContext));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
        Assert.Equal("download_capacity_exceeded", exception.ErrorCode);
        Assert.Equal("5", rejectedContext.Response.Headers.RetryAfter);
        Assert.Single(tracking.Starts);

        origin.ReleaseFirstRequest.TrySetResult();
        await first;
    }

    [Fact]
    public async Task Stream_IfNoneMatch_ReturnsNotModifiedWithoutOriginOrAnalytics()
    {
        const long size = 32 * 1024;
        using var cache = new TemporaryCacheDirectory();
        var sha256 = PatternSha256(size);
        var tracking = new TrackingService(Artifact(size, sha256));
        using var metrics = new PeerOnQMetrics(new PeerOnQMetricState());
        var origin = new PatternOriginHandler(size);
        var service = Service(tracking, origin, metrics, cache.Path);
        var context = Context();
        context.Request.Headers.IfNoneMatch = $"\"sha256-{sha256.ToLowerInvariant()}\"";

        await StreamAsync(service, context);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Empty(origin.Requests);
        Assert.Empty(tracking.Starts);
    }

    [Fact]
    public void Cache_StartupRemovesAbandonedPartials()
    {
        using var cache = new TemporaryCacheDirectory();
        File.WriteAllText(System.IO.Path.Combine(cache.Path, $"{new string('a', 64)}.{Guid.NewGuid():N}.partial"), "abandoned");
        var settings = Options.Create(Settings(cache.Path));

        _ = new VerifiedArtifactCache(settings, TimeProvider.System);

        Assert.Empty(cache.Partials);
    }

    [Fact]
    public void Cache_StartupRejectsUnsafePartialDirectory()
    {
        using var cache = new TemporaryCacheDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(cache.Path, $"{Guid.NewGuid():N}.partial"));
        var settings = Options.Create(Settings(cache.Path));

        Assert.Throws<InvalidOperationException>(() => new VerifiedArtifactCache(settings, TimeProvider.System));
    }

    private static Task StreamAsync(
        DownloadStreamingService service,
        DefaultHttpContext context,
        CancellationToken cancellationToken = default) =>
        service.StreamAsync(
            context,
            PlatformKindV1.Windows,
            ArchitectureKindV1.X64,
            InstallChannelV1.Stable,
            null,
            cancellationToken);

    private static DownloadStreamingService Service(
        TrackingService tracking,
        HttpMessageHandler handler,
        PeerOnQMetrics metrics,
        string cacheDirectory,
        long maximumArtifactBytes = MiB,
        int maximumConcurrentStreams = 4)
    {
        var values = Settings(cacheDirectory);
        values.MaximumArtifactBytes = maximumArtifactBytes;
        values.MaximumCacheBytes = Math.Max(maximumArtifactBytes * 2, 2 * MiB);
        values.MaximumConcurrentStreams = maximumConcurrentStreams;
        var settings = Options.Create(values);
        var time = TimeProvider.System;
        return new DownloadStreamingService(
            tracking,
            new TestHttpClientFactory(new HttpClient(handler)),
            settings,
            time,
            metrics,
            new DownloadStreamGate(settings),
            new VerifiedArtifactCache(settings, time));
    }

    private static DownloadsOptions Settings(string cacheDirectory) => new()
    {
        AllowedArtifactHosts = ["artifacts.peeronq.test"],
        CompletionTokenKey = new string('k', 64),
        MaximumArtifactBytes = MiB,
        MaximumCacheBytes = 2 * MiB,
        MaximumConcurrentStreams = 4,
        StreamBufferBytes = 64 * 1024,
        StreamDeadlineMinutes = 5,
        CacheDirectory = cacheDirectory,
    };

    private static DefaultHttpContext Context(Stream? responseBody = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = responseBody ?? new MemoryStream();
        return context;
    }

    private static DownloadArtifact Artifact(long size, string sha256) => new(
        "1.2.3",
        PlatformKind.Windows,
        ArchitectureKind.X64,
        InstallChannel.Stable,
        new Uri("https://artifacts.peeronq.test/releases/peeronq.msi"),
        sha256,
        new string('a', 64),
        size);

    private static string PatternSha256(long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new PatternStream(0, length);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = stream.Read(buffer);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] PatternBytes(long start, int length)
    {
        var bytes = new byte[length];
        using var stream = new PatternStream(start, length);
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static async Task WriteCacheFileAsync(string path, int size, CancellationToken cancellationToken)
    {
        var bytes = new byte[size];
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    private sealed class TrackingService(DownloadArtifact artifact) : IDownloadTrackingService
    {
        public ConcurrentQueue<DownloadStartRequestV1> Starts { get; } = [];
        public ConcurrentQueue<DownloadCompleteRequestV1> Completions { get; } = [];
        public TaskCompletionSource TwoStarts { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DownloadStartResultV1> StartAsync(DownloadStartRequestV1 request, string? privacyScopedUniquenessValue, CancellationToken cancellationToken = default)
        {
            Starts.Enqueue(request);
            if (Starts.Count >= 2) TwoStarts.TrySetResult();
            return Task.FromResult(new DownloadStartResultV1(request.DownloadId, DateTimeOffset.UtcNow, false));
        }

        public Task CompleteAsync(DownloadCompleteRequestV1 request, CancellationToken cancellationToken = default)
        {
            Completions.Enqueue(request);
            return Task.CompletedTask;
        }

        public Task<DownloadArtifact?> ResolveLatestAsync(PlatformKindV1 platform, ArchitectureKindV1 architecture, InstallChannelV1 channel, CancellationToken cancellationToken = default) =>
            Task.FromResult<DownloadArtifact?>(artifact);

        public Task<DownloadArtifact?> ResolveVersionAsync(PlatformKindV1 platform, ArchitectureKindV1 architecture, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<DownloadArtifact?>(artifact);
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class PatternOriginHandler(long size) : HttpMessageHandler
    {
        public const string EntityTag = "\"origin-v1\"";
        public ConcurrentQueue<bool> Requests { get; } = [];
        public bool TamperContent { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(true);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PatternStream(0, size, TamperContent)),
            };
            response.Headers.ETag = EntityTagHeaderValue.Parse(EntityTag);
            response.Content.Headers.ContentLength = size;
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingOriginHandler(long size) : HttpMessageHandler
    {
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);
        public TaskCompletionSource FirstRequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                FirstRequestEntered.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PatternStream(0, size)),
            };
            response.Headers.ETag = EntityTagHeaderValue.Parse(PatternOriginHandler.EntityTag);
            response.Content.Headers.ContentLength = size;
            return response;
        }
    }

    private sealed class InterruptibleOriginHandler(long size) : HttpMessageHandler
    {
        private int _requests;
        public TaskCompletionSource FirstChunkWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var first = Interlocked.Increment(ref _requests) == 1;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(first
                    ? new InterruptiblePatternStream(size, FirstChunkWritten)
                    : new PatternStream(0, size)),
            };
            response.Headers.ETag = EntityTagHeaderValue.Parse(PatternOriginHandler.EntityTag);
            response.Content.Headers.ContentLength = size;
            return Task.FromResult(response);
        }
    }

    private sealed class InterruptiblePatternStream(
        long length,
        TaskCompletionSource firstChunkWritten) : Stream
    {
        private bool _firstRead = true;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_firstRead) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            _firstRead = false;
            var count = (int)Math.Min(buffer.Length, length);
            for (var index = 0; index < count; index++) buffer.Span[index] = (byte)((index * 31 + 17) % 251);
            Position += count;
            firstChunkWritten.TrySetResult();
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PatternStream(long start, long length, bool tamper = false) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - _position);
            for (var index = 0; index < count; index++)
            {
                var value = PatternByte(start + _position + index);
                buffer[index] = tamper ? (byte)(value ^ 0x5a) : value;
            }
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        private static byte PatternByte(long position) => (byte)((position * 31 + 17) % 251);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long BytesWritten { get; private set; }
        public string Sha256 => Convert.ToHexString(_hash.GetCurrentHash());

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            BytesWritten += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class TemporaryCacheDirectory : IDisposable
    {
        public TemporaryCacheDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "peeronq-download-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string[] Artifacts => Directory.GetFiles(Path, "*.artifact", SearchOption.TopDirectoryOnly);
        public string[] Partials => Directory.GetFiles(Path, "*.partial", SearchOption.TopDirectoryOnly);

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            foreach (var file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
    }
}
