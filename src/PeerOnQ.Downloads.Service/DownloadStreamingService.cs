using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Observability;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Downloads.Service;

public sealed class DownloadStreamingService(
    IDownloadTrackingService tracking,
    IHttpClientFactory clients,
    IOptions<DownloadsOptions> options,
    TimeProvider timeProvider,
    PeerOnQMetrics metrics,
    DownloadStreamGate streamGate,
    VerifiedArtifactCache verifiedArtifacts)
{
    public async Task StreamAsync(
        HttpContext context,
        PlatformKindV1 platform,
        ArchitectureKindV1 architecture,
        InstallChannelV1 channel,
        string? version,
        CancellationToken cancellationToken)
    {
        var artifact = version is null
            ? await tracking.ResolveLatestAsync(platform, architecture, channel, cancellationToken)
            : await tracking.ResolveVersionAsync(platform, architecture, version, cancellationToken);
        if (artifact is null) throw new KeyNotFoundException("The requested release artifact does not exist.");
        ValidateArtifactUri(artifact.ArtifactUri);
        if (artifact.SizeBytes <= 0 || artifact.SizeBytes > options.Value.MaximumArtifactBytes)
            throw new CloudServiceException("ARTIFACT_SIZE_INVALID", "The release artifact size is invalid.");

        var expectedHash = ParseDigest(artifact.Sha256, "ARTIFACT_DIGEST_INVALID");
        _ = ParseDigest(artifact.SignedManifestDigest, "ARTIFACT_MANIFEST_DIGEST_INVALID");
        var digestHex = Convert.ToHexString(expectedHash).ToLowerInvariant();
        var responseEntityTag = $"\"sha256-{digestHex}\"";

        if (MatchesIfNoneMatch(context, responseEntityTag))
        {
            SetCacheHeaders(context, responseEntityTag);
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        var requestedRange = ParseRequestedRange(context, artifact.SizeBytes, responseEntityTag);
        using var streamLease = await streamGate.TryAcquireAsync(cancellationToken);
        if (streamLease is null)
        {
            context.Response.Headers.RetryAfter = "5";
            throw new ApiProblemException(
                StatusCodes.Status503ServiceUnavailable,
                "download_capacity_exceeded",
                "Download capacity is temporarily exhausted.");
        }

        using var streamDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        streamDeadline.CancelAfter(TimeSpan.FromMinutes(options.Value.StreamDeadlineMinutes));
        var streamCancellationToken = streamDeadline.Token;

        var downloadId = Guid.NewGuid();
        var started = new DownloadStartRequestV1(
            downloadId,
            platform,
            architecture,
            artifact.Version,
            channel,
            DownloadRequestPrivacy.NormalizeSource(context.Request.Query["source"].FirstOrDefault()),
            DownloadRequestPrivacy.NormalizeCampaign(context.Request.Query["campaign"].FirstOrDefault()),
            null,
            DownloadRequestPrivacy.UserAgentFamily(context.Request.Headers.UserAgent),
            $"stream:{downloadId:N}");
        await tracking.StartAsync(started, DownloadRequestPrivacy.UniquenessScope(context, timeProvider), streamCancellationToken);
        metrics.DownloadStarted(platform.ToString(), architecture.ToString());

        var completed = false;
        try
        {
            await using var cached = await OpenVerifiedCacheAsync(
                artifact,
                digestHex,
                expectedHash,
                streamCancellationToken);
            await StreamCachedArtifactAsync(
                context,
                cached.Stream,
                artifact,
                architecture,
                responseEntityTag,
                downloadId,
                requestedRange,
                streamCancellationToken);
            completed = true;
        }
        finally
        {
            using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await tracking.CompleteAsync(new DownloadCompleteRequestV1(
                    downloadId,
                    completed
                        ? requestedRange is null ? DownloadResultV1.Completed : DownloadResultV1.Partial
                        : cancellationToken.IsCancellationRequested ? DownloadResultV1.Cancelled : DownloadResultV1.Failed,
                    timeProvider.GetUtcNow()), completionTimeout.Token);
                if (completed) metrics.DownloadCompleted(requestedRange is null ? "success" : "partial");
            }
            catch (Exception exception)
            {
                context.RequestServices.GetRequiredService<ILogger<DownloadStreamingService>>().LogWarning(
                    "Download completion telemetry failed with {EventName} and {ExceptionType}",
                    "download.completion.telemetry_failed",
                    exception.GetType().Name);
            }
        }
    }

    private async Task<VerifiedArtifactLease> OpenVerifiedCacheAsync(
        DownloadArtifact artifact,
        string digestHex,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await verifiedArtifacts.OpenOrFillAsync(
                digestHex,
                artifact.SizeBytes,
                (partialPath, fillCancellationToken) => FillVerifiedCacheAsync(
                    artifact,
                    expectedHash,
                    partialPath,
                    fillCancellationToken),
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CloudServiceException(
                "ARTIFACT_CACHE_UNAVAILABLE",
                "The verified artifact cache is temporarily unavailable.",
                isPermanent: false);
        }
    }

    private async Task FillVerifiedCacheAsync(
        DownloadArtifact artifact,
        byte[] expectedHash,
        string partialPath,
        CancellationToken cancellationToken)
    {
        using var request = CreateOriginRequest(artifact.ArtifactUri);
        using var response = await clients.CreateClient("release-artifacts")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK) throw OriginUnavailable();
        ValidateIdentityEncoding(response);
        if (response.Content.Headers.ContentLength != artifact.SizeBytes)
            throw new CloudServiceException("ARTIFACT_SIZE_MISMATCH", "The release artifact metadata did not match.", isPermanent: false);

        var originEntityTag = response.Headers.ETag;
        if (originEntityTag is null || originEntityTag.IsWeak || originEntityTag.Tag == "*")
            throw new CloudServiceException("ARTIFACT_ORIGIN_UNVERSIONED", "The release artifact origin did not provide a strong entity tag.", isPermanent: false);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            options.Value.StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(options.Value.StreamBufferBytes);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, options.Value.StreamBufferBytes), cancellationToken);
                if (read == 0) break;
                total = checked(total + read);
                if (total > artifact.SizeBytes || total > options.Value.MaximumArtifactBytes)
                    throw new CloudServiceException("ARTIFACT_SIZE_MISMATCH", "The release artifact metadata did not match.", isPermanent: false);
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total != artifact.SizeBytes)
                throw new CloudServiceException("ARTIFACT_SIZE_MISMATCH", "The release artifact metadata did not match.", isPermanent: false);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash))
                throw new CloudServiceException("ARTIFACT_INTEGRITY_MISMATCH", "The release artifact integrity validation failed.", isPermanent: false);

            await target.FlushAsync(cancellationToken);
            target.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task StreamCachedArtifactAsync(
        HttpContext context,
        FileStream source,
        DownloadArtifact artifact,
        ArchitectureKindV1 architecture,
        string responseEntityTag,
        Guid downloadId,
        RequestedRange? requestedRange,
        CancellationToken cancellationToken)
    {
        var responseBytes = requestedRange?.Length ?? artifact.SizeBytes;
        if (requestedRange is not null) source.Seek(requestedRange.Start, SeekOrigin.Begin);

        context.Response.StatusCode = requestedRange is null
            ? StatusCodes.Status200OK
            : StatusCodes.Status206PartialContent;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = responseBytes;
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"PeerOnQ-{SafeVersion(artifact.Version)}-{architecture.ToString().ToLowerInvariant()}.msi\"";
        context.Response.Headers["X-PeerOnQ-Download-ID"] = downloadId.ToString("D");
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        SetCacheHeaders(context, responseEntityTag);
        if (requestedRange is not null)
            context.Response.Headers.ContentRange = $"bytes {requestedRange.Start}-{requestedRange.End}/{artifact.SizeBytes}";

        await CopyExactAsync(source, context.Response.Body, responseBytes, cancellationToken);
    }

    private async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long bytesToCopy,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(options.Value.StreamBufferBytes);
        try
        {
            var remaining = bytesToCopy;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(remaining, options.Value.StreamBufferBytes);
                var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                if (read == 0)
                    throw new CloudServiceException("ARTIFACT_CACHE_TRUNCATED", "The verified artifact cache entry is incomplete.", isPermanent: false);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static HttpRequestMessage CreateOriginRequest(Uri artifactUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, artifactUri);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        return request;
    }

    private static RequestedRange? ParseRequestedRange(HttpContext context, long artifactSize, string responseEntityTag)
    {
        var rawRange = context.Request.Headers.Range.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawRange)) return null;
        if (!string.IsNullOrWhiteSpace(context.Request.Headers.IfRange)
            && !string.Equals(context.Request.Headers.IfRange.ToString().Trim(), responseEntityTag, StringComparison.Ordinal))
        {
            return null;
        }

        if (!RangeHeaderValue.TryParse(rawRange, out var parsed)
            || !string.Equals(parsed.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || parsed.Ranges.Count != 1)
        {
            throw RangeNotSatisfiable(context, artifactSize);
        }

        var item = parsed.Ranges.Single();
        long start;
        long end;
        if (item.From is { } from)
        {
            if (from >= artifactSize || item.To < from) throw RangeNotSatisfiable(context, artifactSize);
            start = from;
            end = Math.Min(item.To ?? artifactSize - 1, artifactSize - 1);
        }
        else if (item.To is { } suffixLength && suffixLength > 0)
        {
            start = Math.Max(0, artifactSize - suffixLength);
            end = artifactSize - 1;
        }
        else
        {
            throw RangeNotSatisfiable(context, artifactSize);
        }

        return new RequestedRange(start, end);
    }

    private static ApiProblemException RangeNotSatisfiable(HttpContext context, long artifactSize)
    {
        context.Response.Headers.ContentRange = $"bytes */{artifactSize}";
        return new ApiProblemException(
            StatusCodes.Status416RangeNotSatisfiable,
            "range_not_satisfiable",
            "The requested byte range is not satisfiable.");
    }

    private static bool MatchesIfNoneMatch(HttpContext context, string responseEntityTag)
    {
        foreach (var value in context.Request.Headers.IfNoneMatch)
        {
            if (value is null) continue;
            foreach (var candidate in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = candidate.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
                    ? candidate[2..]
                    : candidate;
                if (normalized == "*" || string.Equals(normalized, responseEntityTag, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static void SetCacheHeaders(HttpContext context, string responseEntityTag)
    {
        context.Response.Headers.ETag = responseEntityTag;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
    }

    private static void ValidateIdentityEncoding(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(value => !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new CloudServiceException("ARTIFACT_ORIGIN_ENCODING_INVALID", "The release artifact origin returned an encoded representation.", isPermanent: false);
    }

    private static byte[] ParseDigest(string value, string code)
    {
        byte[] digest;
        try
        {
            digest = Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            throw new CloudServiceException(code, "The release artifact digest is invalid.");
        }

        if (digest.Length != 32)
            throw new CloudServiceException(code, "The release artifact digest is invalid.");
        return digest;
    }

    private static CloudServiceException OriginUnavailable() =>
        new("ARTIFACT_ORIGIN_UNAVAILABLE", "The release artifact is temporarily unavailable.", isPermanent: false);

    private void ValidateArtifactUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && !(options.Value.AllowHttpArtifacts && uri.Scheme == Uri.UriSchemeHttp)))
            throw new CloudServiceException("ARTIFACT_ORIGIN_INVALID", "The release artifact origin is invalid.");
        if (!options.Value.AllowedArtifactHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new CloudServiceException("ARTIFACT_ORIGIN_INVALID", "The release artifact origin is not allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new CloudServiceException("ARTIFACT_ORIGIN_INVALID", "The release artifact origin is invalid.");
    }

    private static string SafeVersion(string value) =>
        value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            ? value
            : "release";

    private sealed record RequestedRange(long Start, long End)
    {
        public long Length => End - Start + 1;
    }
}
