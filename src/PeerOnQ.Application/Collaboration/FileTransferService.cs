using System.Diagnostics;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerOnQ.Application.Collaboration;

public enum TransferStatus
{
    Offered = 0,
    AwaitingDecision = 1,
    Queued = 2,
    Transferring = 3,
    Paused = 4,
    Completed = 5,
    Rejected = 6,
    Canceled = 7,
    Failed = 8,
}

public sealed record FileTransferOptions
{
    // Byte quotas are administrator controls. PeerOnQ has no product-level size ceiling.
    public long MaximumFileBytes { get; init; } = long.MaxValue;
    public long MaximumTransferBytes { get; init; } = long.MaxValue;
    public int MaximumEntries { get; init; } = 100_000;
    public int ChunkBytes { get; init; } = 256 * 1024;
    public int MaximumParallelTransfers { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    public bool RequireMalwareScan { get; init; }
}

public enum MalwareScanResult
{
    Clean = 0,
    ThreatDetected = 1,
    Unavailable = 2,
}

public interface IMalwareScanner
{
    Task<MalwareScanResult> ScanAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed class NoOpMalwareScanner : IMalwareScanner
{
    public static NoOpMalwareScanner Instance { get; } = new();
    public Task<MalwareScanResult> ScanAsync(string filePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(MalwareScanResult.Unavailable);
}

public sealed record TransferSnapshot(
    Guid TransferId,
    bool Incoming,
    string DisplayName,
    TransferStatus Status,
    long TotalBytes,
    long TransferredBytes,
    double BytesPerSecond,
    TimeSpan? Remaining,
    string? FailureCode);

public sealed class FileTransferService : IAsyncDisposable
{
    private readonly ICollaborationTransport _transport;
    private readonly ISecurityAuditLog? _audit;
    private readonly IMalwareScanner _malwareScanner;
    private readonly FileTransferOptions _options;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _outboundQueue;
    private readonly SemaphoreSlim _inboundMessages = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, TransferRuntime> _transfers = [];
    private long _lastProgressNotificationTimestamp;
    private int _disposed;

    public FileTransferService(
        ICollaborationTransport transport,
        FileTransferOptions? options = null,
        IMalwareScanner? malwareScanner = null,
        ISecurityAuditLog? audit = null,
        ILogger<FileTransferService>? logger = null)
    {
        _transport = transport;
        _options = options ?? new FileTransferOptions();
        if (_options.MaximumFileBytes <= 0 || _options.MaximumTransferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Transfer byte quotas must be positive.");
        if (_options.MaximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The manifest entry limit must be positive.");
        if (_options.ChunkBytes is <= 0 or > CollaborationProtocolCodec.MaximumChunkBytes)
            throw new ArgumentOutOfRangeException(nameof(options), "The chunk size exceeds the protocol bound.");
        if (_options.MaximumParallelTransfers is <= 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(options), "Parallel transfer workers must be between 1 and 32.");
        _outboundQueue = new SemaphoreSlim(
            _options.MaximumParallelTransfers,
            _options.MaximumParallelTransfers);
        _malwareScanner = malwareScanner ?? NoOpMalwareScanner.Instance;
        _audit = audit;
        _log = logger ?? NullLogger<FileTransferService>.Instance;
        _transport.MessageReceived += OnMessageReceived;
        _transport.Ready += OnTransportReady;
    }

    public event EventHandler<TransferOffer>? IncomingOffer;
    public event EventHandler<TransferSnapshot>? TransferChanged;

    public IReadOnlyList<TransferSnapshot> History
    {
        get
        {
            lock (_gate) return _transfers.Values.Select(Snapshot).ToArray();
        }
    }

    public async Task<Guid> OfferAsync(IReadOnlyList<string> sourcePaths, CancellationToken cancellationToken = default)
    {
        EnsureFileTransferPermission();
        if (sourcePaths.Count == 0) throw new ArgumentException("Select at least one file or folder.", nameof(sourcePaths));

        var transferId = Guid.NewGuid();
        var (entries, sources) = await BuildManifestAsync(sourcePaths, cancellationToken);
        var total = CalculateTotalBytes(entries);
        var displayName = entries.Count == 1 ? Path.GetFileName(entries[0].RelativePath) : $"{entries.Count} items";
        var offer = new TransferOffer
        {
            TransferId = transferId,
            DisplayName = displayName,
            TotalBytes = total,
            Entries = entries,
        };

        var runtime = TransferRuntime.Outbound(offer, sources, _options.ChunkBytes);
        Add(runtime);
        await _transport.SendAsync(offer, cancellationToken);
        await AuditAsync(SecurityAuditEventType.FileTransferOffered, transferId, "sent");
        return transferId;
    }

    public async Task AcceptAsync(
        Guid transferId,
        string destinationDirectory,
        TransferCollisionPolicy collisionPolicy,
        CancellationToken cancellationToken = default)
    {
        EnsureFileTransferPermission();
        if (collisionPolicy == TransferCollisionPolicy.Ask)
            throw new ArgumentException("A concrete collision policy is required.", nameof(collisionPolicy));
        if (!Directory.Exists(destinationDirectory))
            throw new DirectoryNotFoundException("The selected destination does not exist.");

        TransferRuntime runtime;
        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId, out runtime!) || !runtime.Incoming || runtime.Status != TransferStatus.AwaitingDecision)
                throw new InvalidOperationException("The incoming offer is no longer awaiting a decision.");
        }

        EnsureAvailableDisk(destinationDirectory, runtime.Offer.TotalBytes);
        try
        {
            PrepareDestinations(runtime, destinationDirectory, collisionPolicy);
        }
        catch
        {
            runtime.ResetPreparedDestination();
            throw;
        }
        runtime.Status = TransferStatus.Queued;
        Notify(runtime);
        await _transport.SendAsync(new TransferAccept
        {
            TransferId = transferId,
            CollisionPolicy = collisionPolicy,
            ReceivedOffsets = runtime.ReceivedOffsets,
        }, cancellationToken);
        await AuditAsync(SecurityAuditEventType.FileTransferAccepted, transferId, "accepted");
    }

    public Task RejectAsync(Guid transferId, string reasonCode = "user_rejected", CancellationToken cancellationToken = default) =>
        FinishByControlAsync(transferId, TransferStatus.Rejected,
            new TransferReject { TransferId = transferId, ReasonCode = SanitizeReason(reasonCode) ?? "user_rejected" },
            SecurityAuditEventType.FileTransferRejected, cancellationToken);

    public async Task PauseAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        var runtime = GetActive(transferId);
        runtime.Pause();
        Notify(runtime);
        await _transport.SendAsync(new TransferPause { TransferId = transferId }, cancellationToken);
    }

    public async Task ResumeAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        var runtime = GetActive(transferId);
        runtime.Resume();
        Notify(runtime);
        await _transport.SendAsync(new TransferResume
        {
            TransferId = transferId,
            ReceivedOffsets = runtime.ReceivedOffsets,
        }, cancellationToken);
    }

    public Task CancelAsync(Guid transferId, CancellationToken cancellationToken = default) =>
        FinishByControlAsync(transferId, TransferStatus.Canceled,
            new TransferCancel { TransferId = transferId },
            SecurityAuditEventType.FileTransferCanceled, cancellationToken);

    public void ConnectionInterrupted()
    {
        TransferRuntime[] active;
        lock (_gate)
            active = _transfers.Values.Where(item => item.Status is TransferStatus.Queued or TransferStatus.Transferring).ToArray();
        foreach (var runtime in active)
        {
            runtime.Pause();
            Notify(runtime);
        }
    }

    public void ConnectionResumed() => OnTransportReady(this, EventArgs.Empty);

    private async void OnTransportReady(object? sender, EventArgs args)
    {
        TransferRuntime[] paused;
        lock (_gate) paused = _transfers.Values.Where(item => item.Status == TransferStatus.Paused).ToArray();

        foreach (var runtime in paused.Where(item => item.Incoming))
        {
            runtime.Resume();
            Notify(runtime);
            try
            {
                await _transport.SendAsync(new TransferResume
                {
                    TransferId = runtime.Offer.TransferId,
                    ReceivedOffsets = runtime.ReceivedOffsets,
                });
            }
            catch (Exception ex)
            {
                runtime.Pause();
                Notify(runtime);
                _log.LogWarning("Could not request transfer resume after reconnect ({ErrorType})", ex.GetType().Name);
            }
        }
    }

    private void OnMessageReceived(object? sender, CollaborationMessage message)
    {
        if (message is not (TransferOffer or TransferAccept or TransferReject or TransferChunk
            or TransferPause or TransferResume or TransferCancel or TransferComplete or TransferFailed))
        {
            return;
        }

        if (message is not (TransferChunk or TransferComplete))
        {
            // Control records are infrequent and must not serialize the start of independent
            // transfer workers. The bulk records below are the only unbounded-rate input.
            _ = ProcessInboundMessageAsync(message);
            return;
        }

        try
        {
            // Collaboration transport dispatch is synchronous for relay records. Waiting for
            // bulk records bounds queued chunks by the receiver's actual disk/decrypt throughput
            // instead of creating unbounded async-void work items during a high-speed transfer.
            ProcessInboundMessageAsync(message).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Rejected or failed a file-transfer message ({ErrorType})", ex.GetType().Name);
        }
    }

    private async Task ProcessInboundMessageAsync(CollaborationMessage message)
    {
        await _inboundMessages.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            try
            {
                switch (message)
                {
                    case TransferOffer offer:
                        await HandleOfferAsync(offer);
                        break;
                    case TransferAccept accept:
                        await HandleAcceptAsync(accept);
                        break;
                    case TransferReject reject:
                        SetTerminal(reject.TransferId, TransferStatus.Rejected, reject.ReasonCode);
                        _transport.CompleteTransfer(reject.TransferId);
                        break;
                    case TransferChunk chunk:
                        await HandleChunkAsync(chunk);
                        break;
                    case TransferPause pause:
                        GetActive(pause.TransferId).Pause();
                        Notify(GetActive(pause.TransferId));
                        break;
                    case TransferResume resume:
                        var resumed = GetActive(resume.TransferId);
                        resumed.SetRemoteOffsets(resume.ReceivedOffsets);
                        resumed.Resume();
                        Notify(resumed);
                        break;
                    case TransferCancel cancel:
                        SetTerminal(cancel.TransferId, TransferStatus.Canceled, cancel.ReasonCode);
                        _transport.CompleteTransfer(cancel.TransferId);
                        break;
                    case TransferComplete complete:
                        await HandleCompleteAsync(complete.TransferId);
                        break;
                    case TransferFailed failed:
                        SetTerminal(failed.TransferId, TransferStatus.Failed, failed.ReasonCode);
                        _transport.CompleteTransfer(failed.TransferId);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Rejected or failed a file-transfer message ({ErrorType})", ex.GetType().Name);
                if (message is TransferOffer or TransferChunk or TransferComplete)
                {
                    var id = message switch
                    {
                        TransferOffer offer => offer.TransferId,
                        TransferChunk chunk => chunk.TransferId,
                        TransferComplete complete => complete.TransferId,
                        _ => Guid.Empty,
                    };
                    if (id != Guid.Empty)
                    {
                        var failureCode = ClassifyFailure(ex);
                        SetTerminal(id, TransferStatus.Failed, failureCode);
                        try { await _transport.SendAsync(new TransferFailed { TransferId = id, ReasonCode = failureCode }); }
                        catch (Exception sendError) { _log.LogDebug("Could not send transfer failure ({ErrorType})", sendError.GetType().Name); }
                        finally { _transport.CompleteTransfer(id); }
                    }
                }
            }
        }
        finally
        {
            _inboundMessages.Release();
        }
    }

    private Task HandleOfferAsync(TransferOffer offer)
    {
        ValidateOffer(offer);
        offer = offer with
        {
            Entries = offer.Entries.Select(entry => entry with
            {
                RelativePath = SafeTransferPath.NormalizeRelative(entry.RelativePath),
            }).ToArray(),
        };
        var runtime = TransferRuntime.Inbound(offer, _options.ChunkBytes);
        Add(runtime);
        IncomingOffer?.Invoke(this, offer);
        return AuditAsync(SecurityAuditEventType.FileTransferOffered, offer.TransferId, "received");
    }

    private Task HandleAcceptAsync(TransferAccept accept)
    {
        TransferRuntime runtime;
        lock (_gate)
        {
            if (!_transfers.TryGetValue(accept.TransferId, out runtime!) || runtime.Incoming || runtime.Status != TransferStatus.Offered)
                throw new InvalidOperationException("Unexpected transfer acceptance.");
            runtime.SetRemoteOffsets(accept.ReceivedOffsets);
            runtime.Status = TransferStatus.Queued;
        }

        Notify(runtime);
        _ = Task.Run(() => SendOutboundAsync(runtime));
        return Task.CompletedTask;
    }

    private async Task SendOutboundAsync(TransferRuntime runtime)
    {
        await _outboundQueue.WaitAsync(runtime.Cancellation.Token);
        try
        {
            runtime.Status = TransferStatus.Transferring;
            runtime.StartClock();
            Notify(runtime);

            foreach (var entry in runtime.Offer.Entries.Where(entry => entry.Kind == TransferItemKind.File))
            {
                var source = runtime.SourcePaths[entry.RelativePath];
                await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, _options.ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var offset = runtime.RemoteOffsets.GetValueOrDefault(entry.RelativePath);
                if (offset < 0 || offset > stream.Length) throw new InvalidDataException("Invalid resume offset.");
                stream.Position = offset;
                var index = offset / _options.ChunkBytes;
                var buffer = new byte[_options.ChunkBytes];

                while (stream.Position < stream.Length)
                {
                    await runtime.WaitWhilePausedAsync();
                    var read = await stream.ReadAsync(buffer, runtime.Cancellation.Token);
                    if (read == 0) break;
                    var payload = buffer.AsSpan(0, read).ToArray();
                    await _transport.SendAsync(new TransferChunk
                    {
                        TransferId = runtime.Offer.TransferId,
                        RelativePath = entry.RelativePath,
                        Offset = stream.Position - read,
                        Index = index++,
                        ChunkSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                        Payload = payload,
                    }, runtime.Cancellation.Token);
                    runtime.AddProgress(read);
                    Notify(runtime);
                }
            }

            await _transport.SendAsync(new TransferComplete { TransferId = runtime.Offer.TransferId }, runtime.Cancellation.Token);
        }
        catch (OperationCanceledException) when (runtime.Status == TransferStatus.Canceled)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning("Outbound transfer failed ({ErrorType})", ex.GetType().Name);
            SetTerminal(runtime.Offer.TransferId, TransferStatus.Failed, "send_failed");
            try { await _transport.SendAsync(new TransferFailed { TransferId = runtime.Offer.TransferId, ReasonCode = "send_failed" }); }
            catch (Exception sendError) { _log.LogDebug("Could not send transfer failure ({ErrorType})", sendError.GetType().Name); }
            finally { _transport.CompleteTransfer(runtime.Offer.TransferId); }
        }
        finally
        {
            _outboundQueue.Release();
        }
    }

    private async Task HandleChunkAsync(TransferChunk chunk)
    {
        var runtime = GetActive(chunk.TransferId);
        if (!runtime.Incoming || runtime.Status is not (TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Paused))
            throw new InvalidOperationException("Unexpected transfer chunk.");
        var remainPaused = runtime.Status == TransferStatus.Paused;

        var relative = SafeTransferPath.NormalizeRelative(chunk.RelativePath);
        var entry = runtime.Offer.Entries.SingleOrDefault(item =>
            item.Kind == TransferItemKind.File && string.Equals(item.RelativePath, relative, StringComparison.Ordinal));
        if (entry is null || chunk.Payload.Length is 0 or > CollaborationProtocolCodec.MaximumChunkBytes)
            throw new InvalidDataException("Chunk metadata is invalid.");
        if (chunk.ChunkSha256.Length != 64 || !chunk.ChunkSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Chunk integrity metadata is invalid.");
        var actualChunkHash = Convert.ToHexString(SHA256.HashData(chunk.Payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualChunkHash), Convert.FromHexString(chunk.ChunkSha256)))
            throw new InvalidDataException("Chunk integrity verification failed.");

        if (!runtime.Destinations.TryGetValue(relative, out var destination) || string.IsNullOrEmpty(destination))
            return;
        var partial = runtime.PartialPaths[relative];
        EnsureDestinationStillSafe(runtime, partial);
        var expectedOffset = runtime.ReceivedOffsets.GetValueOrDefault(relative);
        var expectedIndex = runtime.ReceivedNextIndexes.GetValueOrDefault(relative);
        if (chunk.Offset != expectedOffset || chunk.Index != expectedIndex)
            throw new InvalidDataException("Chunk checkpoint is invalid, duplicated, or out of order.");
        var stream = runtime.GetWriteStream(relative, partial, _options.ChunkBytes);
        if (stream.Length != chunk.Offset || chunk.Offset > entry.Size - chunk.Payload.Length)
            throw new InvalidDataException("Chunk offset is invalid or out of order.");
        stream.Position = chunk.Offset;
        await stream.WriteAsync(chunk.Payload, runtime.Cancellation.Token);
        // Do not force a physical flush for every 64 KiB record. The partial remains unexposed
        // until the complete-file integrity check succeeds, while per-record write-through
        // throttles otherwise healthy links to storage latency rather than network capacity.
        runtime.ReceivedOffsets[relative] = stream.Position;
        runtime.ReceivedNextIndexes[relative] = chunk.Index + 1;
        if (!remainPaused) runtime.Status = TransferStatus.Transferring;
        runtime.AddProgress(chunk.Payload.Length);
        await _transport.ReportFileDeliveryAsync(
            chunk.TransferId,
            chunk.Payload.Length,
            cancellationToken: runtime.Cancellation.Token);
        Notify(runtime);
    }

    private async Task HandleCompleteAsync(Guid transferId)
    {
        var runtime = GetActive(transferId);
        if (!runtime.Incoming)
        {
            SetTerminal(transferId, TransferStatus.Completed, null);
            _transport.CompleteTransfer(transferId);
            await AuditAsync(SecurityAuditEventType.FileTransferCompleted, transferId, "completed");
            return;
        }

        runtime.CloseWriteStreams();

        foreach (var entry in runtime.Offer.Entries.Where(entry => entry.Kind == TransferItemKind.File))
        {
            if (!runtime.Destinations.TryGetValue(entry.RelativePath, out var destination) || string.IsNullOrEmpty(destination)) continue;
            var partial = runtime.PartialPaths[entry.RelativePath];
            EnsureDestinationStillSafe(runtime, partial);
            EnsureDestinationStillSafe(runtime, destination, allowMissingLeaf: true);
            var info = new FileInfo(partial);
            if (!info.Exists || info.Length != entry.Size) throw new InvalidDataException("Received file size does not match the offer.");

            await using (var stream = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, runtime.Cancellation.Token)).ToLowerInvariant();
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Received file hash does not match the offer.");
            }

            var scan = await _malwareScanner.ScanAsync(partial, runtime.Cancellation.Token);
            if (scan == MalwareScanResult.ThreatDetected || (_options.RequireMalwareScan && scan != MalwareScanResult.Clean))
                throw new InvalidDataException("The received file did not pass malware policy.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureDestinationStillSafe(runtime, destination, allowMissingLeaf: true);
            File.Move(partial, destination, overwrite: runtime.CollisionPolicy == TransferCollisionPolicy.Overwrite);
            if (entry.ModifiedAt is { } modified) File.SetLastWriteTimeUtc(destination, modified.UtcDateTime);
        }

        // Keep the transfer live until the completion acknowledgement has entered the
        // authenticated transport. Marking it complete first allowed the receiver UI to
        // report success while the sender was still waiting for its final confirmation.
        // More importantly, a transient relay failure in that narrow window could leave
        // the two peers with different terminal states.
        await _transport.ReportFileDeliveryAsync(
            transferId,
            deliveredBytes: 0,
            flush: true,
            cancellationToken: runtime.Cancellation.Token);
        await _transport.SendAsync(new TransferComplete { TransferId = transferId });
        SetTerminal(transferId, TransferStatus.Completed, null);
        _transport.CompleteTransfer(transferId);
        await AuditAsync(SecurityAuditEventType.FileTransferCompleted, transferId, "completed");
    }

    private async Task<(IReadOnlyList<TransferEntry> Entries, Dictionary<string, string> Sources)> BuildManifestAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        var entries = new List<TransferEntry>();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var supplied in sourcePaths)
        {
            var source = Path.GetFullPath(supplied);
            if (SafeTransferPath.IsReparsePoint(source)) throw new InvalidDataException("Symlinks and reparse points are not transferred.");

            if (File.Exists(source))
            {
                await AddFileAsync(source, SafeTransferPath.NormalizeRelative(Path.GetFileName(source)), entries, sources, cancellationToken);
            }
            else if (Directory.Exists(source))
            {
                var rootName = SafeTransferPath.NormalizeRelative(Path.GetFileName(source));
                entries.Add(new TransferEntry { RelativePath = rootName, Kind = TransferItemKind.Folder, Size = 0 });
                foreach (var (path, isDirectory) in EnumerateTreeWithoutReparsePoints(source))
                {
                    var relative = SafeTransferPath.NormalizeRelative(Path.Combine(rootName, Path.GetRelativePath(source, path)));
                    if (isDirectory)
                        entries.Add(new TransferEntry { RelativePath = relative, Kind = TransferItemKind.Folder, Size = 0 });
                    else
                        await AddFileAsync(path, relative, entries, sources, cancellationToken);
                }
            }
            else
            {
                throw new FileNotFoundException("A selected transfer source no longer exists.");
            }

            if (entries.Count > _options.MaximumEntries) throw new InvalidDataException("The transfer has too many entries.");
        }

        if (entries.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidDataException("The selected sources contain colliding names.");
        if (CalculateTotalBytes(entries) > _options.MaximumTransferBytes)
            throw new InvalidDataException("The transfer exceeds the configured size limit.");
        return (entries, sources);
    }

    private static IEnumerable<(string Path, bool IsDirectory)> EnumerateTreeWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Symlinks and reparse points are not transferred.");
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                yield return (path, isDirectory);
                if (isDirectory) pending.Push(path);
            }
        }
    }

    private async Task AddFileAsync(
        string source,
        string relative,
        List<TransferEntry> entries,
        Dictionary<string, string> sources,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(source);
        if (info.Length > _options.MaximumFileBytes) throw new InvalidDataException("A file exceeds the configured size limit.");
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        entries.Add(new TransferEntry
        {
            RelativePath = relative,
            Kind = TransferItemKind.File,
            Size = info.Length,
            Sha256 = hash,
            ModifiedAt = info.LastWriteTimeUtc,
        });
        sources.Add(relative, source);
    }

    private void ValidateOffer(TransferOffer offer)
    {
        if (offer.TransferId == Guid.Empty || offer.Entries.Count is 0 || offer.Entries.Count > _options.MaximumEntries)
            throw new InvalidDataException("Transfer offer metadata is invalid.");
        long total = 0;
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in offer.Entries)
        {
            var path = SafeTransferPath.NormalizeRelative(entry.RelativePath);
            if (!unique.Add(path) || entry.Size < 0 || entry.Size > _options.MaximumFileBytes)
                throw new InvalidDataException("Transfer entry metadata is invalid.");
            if (entry.Kind == TransferItemKind.Folder && (entry.Size != 0 || entry.Sha256 is not null))
                throw new InvalidDataException("Folder metadata is invalid.");
            if (entry.Kind == TransferItemKind.File && (entry.Sha256?.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException("A transfer entry has no valid SHA-256 digest.");
            try { checked { total += entry.Size; } }
            catch (OverflowException) { throw new InvalidDataException("Transfer total exceeds the protocol range."); }
        }
        if (total != offer.TotalBytes || total > _options.MaximumTransferBytes)
            throw new InvalidDataException("Transfer total is invalid.");
    }

    private void PrepareDestinations(TransferRuntime runtime, string destinationDirectory, TransferCollisionPolicy collisionPolicy)
    {
        runtime.DestinationRoot = Path.GetFullPath(destinationDirectory);
        SafeTransferPath.RejectReparsePoints(runtime.DestinationRoot, runtime.DestinationRoot);
        foreach (var entry in runtime.Offer.Entries
                     .OrderBy(item => item.RelativePath.Count(character => character is '/' or '\\'))
                     .ThenBy(item => item.Kind == TransferItemKind.Folder ? 0 : 1))
        {
            var normalized = SafeTransferPath.NormalizeRelative(entry.RelativePath);
            var parentRelative = Path.GetDirectoryName(normalized);
            string path;
            if (!string.IsNullOrEmpty(parentRelative)
                && runtime.Destinations.TryGetValue(parentRelative, out var mappedParent))
            {
                if (string.IsNullOrEmpty(mappedParent))
                {
                    runtime.Destinations[entry.RelativePath] = string.Empty;
                    continue;
                }
                path = Path.Combine(mappedParent, Path.GetFileName(normalized));
                SafeTransferPath.RejectReparsePoints(destinationDirectory, path);
            }
            else
            {
                path = SafeTransferPath.ResolveUnderRoot(destinationDirectory, normalized);
            }
            var resolved = SafeTransferPath.ResolveCollision(path, collisionPolicy);
            runtime.Destinations[entry.RelativePath] = resolved;
            if (string.IsNullOrEmpty(resolved))
            {
                if (entry.Kind == TransferItemKind.File)
                {
                    runtime.ReceivedOffsets[entry.RelativePath] = entry.Size;
                    runtime.AddProgress(entry.Size);
                }
                continue;
            }

            if (entry.Kind == TransferItemKind.Folder)
            {
                if (!Directory.Exists(resolved))
                {
                    Directory.CreateDirectory(resolved);
                    runtime.CreatedDirectories.Add(resolved);
                }
                continue;
            }

            var parent = Path.GetDirectoryName(resolved)!;
            if (!Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
                runtime.CreatedDirectories.Add(parent);
            }
            var partial = resolved + $".peeronq.{runtime.Offer.TransferId:N}.partial";
            if (File.Exists(partial)) throw new IOException("A partial transfer file already exists.");
            using (new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            runtime.PartialPaths[entry.RelativePath] = partial;
            runtime.ReceivedOffsets[entry.RelativePath] = 0;
            runtime.ReceivedNextIndexes[entry.RelativePath] = 0;
        }
        runtime.CollisionPolicy = collisionPolicy;
    }

    private static void EnsureAvailableDisk(string destinationDirectory, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
        if (string.IsNullOrEmpty(root)) throw new IOException("The destination drive could not be resolved.");
        var drive = new DriveInfo(root);
        var available = drive.AvailableFreeSpace;
        var margin = Math.Min(requiredBytes / 20, 512L * 1024 * 1024);
        if (requiredBytes > available || margin > available - requiredBytes)
            throw new IOException("The destination does not have enough free space.");
    }

    private static long CalculateTotalBytes(IEnumerable<TransferEntry> entries)
    {
        long total = 0;
        try
        {
            foreach (var entry in entries) checked { total += entry.Size; }
        }
        catch (OverflowException)
        {
            throw new InvalidDataException("Transfer total exceeds the protocol range.");
        }

        return total;
    }

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException => "destination_unavailable",
        IOException io when IsDiskFull(io) => "disk_full",
        IOException => "io_failed",
        _ => "validation_failed",
    };

    private static bool IsDiskFull(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 0x27 or 0x70;
    }

    private static void EnsureDestinationStillSafe(
        TransferRuntime runtime,
        string candidate,
        bool allowMissingLeaf = false)
    {
        if (string.IsNullOrEmpty(runtime.DestinationRoot) || !Directory.Exists(runtime.DestinationRoot))
            throw new DirectoryNotFoundException("The selected transfer destination is no longer available.");

        var root = Path.GetFullPath(runtime.DestinationRoot);
        var full = Path.GetFullPath(candidate);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The transfer destination escaped the selected root.");
        SafeTransferPath.RejectReparsePoints(root, full);
        if ((!allowMissingLeaf || File.Exists(full) || Directory.Exists(full)) && SafeTransferPath.IsReparsePoint(full))
            throw new InvalidDataException("Reparse-point transfer targets are not allowed.");
    }

    private async Task FinishByControlAsync(
        Guid transferId,
        TransferStatus status,
        CollaborationMessage control,
        SecurityAuditEventType auditType,
        CancellationToken cancellationToken)
    {
        await _inboundMessages.WaitAsync(cancellationToken);
        try { SetTerminal(transferId, status, null); }
        finally { _inboundMessages.Release(); }
        await _transport.SendAsync(control, cancellationToken);
        _transport.CompleteTransfer(transferId);
        await AuditAsync(auditType, transferId, status.ToString().ToLowerInvariant());
    }

    private void SetTerminal(Guid id, TransferStatus status, string? failureCode)
    {
        TransferRuntime? runtime;
        lock (_gate)
        {
            if (!_transfers.TryGetValue(id, out runtime)) return;
            if (runtime.Cancellation.IsCancellationRequested
                || runtime.Status is TransferStatus.Completed or TransferStatus.Rejected or TransferStatus.Canceled or TransferStatus.Failed)
                return;
            runtime.Cancellation.Cancel();
        }
        if (status is TransferStatus.Canceled or TransferStatus.Failed or TransferStatus.Rejected)
        {
            runtime.CloseWriteStreams();
            runtime.DeletePartials();
        }
        lock (_gate)
        {
            runtime.Status = status;
            runtime.FailureCode = SanitizeReason(failureCode);
        }
        Notify(runtime);
        _ = AuditAsync(status == TransferStatus.Failed
            ? SecurityAuditEventType.FileTransferFailed
            : status == TransferStatus.Canceled
                ? SecurityAuditEventType.FileTransferCanceled
                : SecurityAuditEventType.FileTransferRejected, id, status.ToString().ToLowerInvariant());
    }

    private void Add(TransferRuntime runtime)
    {
        lock (_gate)
        {
            if (!_transfers.TryAdd(runtime.Offer.TransferId, runtime))
                throw new InvalidOperationException("Duplicate transfer id.");
        }
        Notify(runtime);
    }

    private TransferRuntime GetActive(Guid id)
    {
        lock (_gate)
        {
            if (!_transfers.TryGetValue(id, out var runtime)
                || runtime.Cancellation.IsCancellationRequested
                || runtime.Status is TransferStatus.Completed or TransferStatus.Rejected or TransferStatus.Canceled or TransferStatus.Failed)
                throw new InvalidOperationException("The transfer is not active.");
            return runtime;
        }
    }

    private void Notify(TransferRuntime runtime)
    {
        // Large transfers can produce hundreds of chunks per second on a gigabit link. Progress
        // snapshots are presentation data, so update at 10 Hz while preserving every state change.
        if (runtime.Status == TransferStatus.Transferring
            && runtime.TransferredBytes < runtime.Offer.TotalBytes)
        {
            var now = Stopwatch.GetTimestamp();
            var last = Volatile.Read(ref _lastProgressNotificationTimestamp);
            if (now - last < Stopwatch.Frequency / 10) return;
            Interlocked.Exchange(ref _lastProgressNotificationTimestamp, now);
        }

        TransferChanged?.Invoke(this, Snapshot(runtime));
    }

    private static TransferSnapshot Snapshot(TransferRuntime runtime)
    {
        var elapsed = runtime.Elapsed;
        var speed = elapsed.TotalSeconds > 0 ? runtime.TransferredBytes / elapsed.TotalSeconds : 0;
        var remainingBytes = Math.Max(0, runtime.Offer.TotalBytes - runtime.TransferredBytes);
        var remainingSeconds = speed > 0 ? remainingBytes / speed : 0;
        TimeSpan? remaining = speed > 0 && remainingSeconds <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(remainingSeconds)
            : null;
        return new TransferSnapshot(
            runtime.Offer.TransferId,
            runtime.Incoming,
            runtime.Offer.DisplayName,
            runtime.Status,
            runtime.Offer.TotalBytes,
            runtime.TransferredBytes,
            speed,
            remaining,
            runtime.FailureCode);
    }

    private void EnsureFileTransferPermission()
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (!_transport.Permissions.HasFlag(SessionPermission.FileTransfer))
            throw new UnauthorizedAccessException("File transfer is not permitted for this session.");
    }

    private Task AuditAsync(SecurityAuditEventType type, Guid transferId, string outcome) =>
        _audit?.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = type,
            OccurredAt = DateTimeOffset.UtcNow,
            SessionId = null,
            Outcome = outcome,
            SafeMetadata = new Dictionary<string, string> { ["transferId"] = transferId.ToString("N") },
        }) ?? Task.CompletedTask;

    private static string? SanitizeReason(string? reason) => string.IsNullOrWhiteSpace(reason)
        ? null
        : new string(reason.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(64).ToArray());

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _transport.MessageReceived -= OnMessageReceived;
        _transport.Ready -= OnTransportReady;
        await _inboundMessages.WaitAsync();
        try
        {
            TransferRuntime[] runtimes;
            lock (_gate) runtimes = _transfers.Values.ToArray();
            foreach (var runtime in runtimes)
            {
                runtime.Cancellation.Cancel();
                runtime.CloseWriteStreams();
                runtime.DeletePartials();
            }
        }
        finally { _inboundMessages.Release(); }
        _outboundQueue.Dispose();
        await Task.CompletedTask;
    }

    private sealed class TransferRuntime
    {
        private TaskCompletionSource _resume = CompletedGate();
        private readonly Stopwatch _clock = new();
        private long _transferredBytes;

        private TransferRuntime(TransferOffer offer, bool incoming, int chunkBytes)
        {
            Offer = offer;
            Incoming = incoming;
            ChunkBytes = chunkBytes;
            Status = incoming ? TransferStatus.AwaitingDecision : TransferStatus.Offered;
        }

        public TransferOffer Offer { get; }
        public bool Incoming { get; }
        public int ChunkBytes { get; }
        public TransferStatus Status { get; set; }
        public string? FailureCode { get; set; }
        public long TransferredBytes => Interlocked.Read(ref _transferredBytes);
        public TimeSpan Elapsed => _clock.Elapsed;
        public CancellationTokenSource Cancellation { get; } = new();
        public Dictionary<string, string> SourcePaths { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Destinations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> PartialPaths { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> ReceivedOffsets { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> ReceivedNextIndexes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> RemoteOffsets { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CreatedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FileStream> _writeStreams = new(StringComparer.Ordinal);
        public string? DestinationRoot { get; set; }
        public TransferCollisionPolicy CollisionPolicy { get; set; }

        public static TransferRuntime Outbound(
            TransferOffer offer,
            Dictionary<string, string> sources,
            int chunkBytes)
        {
            var runtime = new TransferRuntime(offer, false, chunkBytes);
            foreach (var pair in sources) runtime.SourcePaths.Add(pair.Key, pair.Value);
            return runtime;
        }

        public static TransferRuntime Inbound(TransferOffer offer, int chunkBytes) => new(offer, true, chunkBytes);

        public FileStream GetWriteStream(string relativePath, string partialPath, int chunkBytes)
        {
            if (_writeStreams.TryGetValue(relativePath, out var stream)) return stream;

            stream = new FileStream(
                partialPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None,
                Math.Max(chunkBytes, 1024 * 1024),
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _writeStreams.Add(relativePath, stream);
            return stream;
        }

        public void CloseWriteStreams()
        {
            foreach (var stream in _writeStreams.Values)
                stream.Dispose();
            _writeStreams.Clear();
        }
        public void StartClock() { if (!_clock.IsRunning) _clock.Start(); }
        public void AddProgress(long count) { Interlocked.Add(ref _transferredBytes, count); StartClock(); }

        public void Pause()
        {
            if (Status is not (TransferStatus.Queued or TransferStatus.Transferring)) return;
            Status = TransferStatus.Paused;
            _clock.Stop();
            if (_resume.Task.IsCompleted) _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Resume()
        {
            if (Status != TransferStatus.Paused) return;
            Status = TransferStatus.Transferring;
            _clock.Start();
            _resume.TrySetResult();
        }

        public Task WaitWhilePausedAsync() => _resume.Task.WaitAsync(Cancellation.Token);

        public void SetRemoteOffsets(IReadOnlyDictionary<string, long> offsets)
        {
            long confirmed = 0;
            RemoteOffsets.Clear();
            foreach (var pair in offsets)
            {
                var path = SafeTransferPath.NormalizeRelative(pair.Key);
                var entry = Offer.Entries.SingleOrDefault(item => item.Kind == TransferItemKind.File && item.RelativePath == path);
                if (entry is null
                    || pair.Value < 0
                    || pair.Value > entry.Size
                    || (pair.Value != entry.Size && pair.Value % ChunkBytes != 0))
                    throw new InvalidDataException("Invalid resume offset.");
                RemoteOffsets[path] = pair.Value;
                try { checked { confirmed += pair.Value; } }
                catch (OverflowException) { throw new InvalidDataException("Resume offsets exceed the protocol range."); }
            }
            Interlocked.Exchange(ref _transferredBytes, confirmed);
        }

        public void DeletePartials()
        {
            foreach (var partial in PartialPaths.Values)
            {
                try { if (File.Exists(partial)) File.Delete(partial); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (var directory in CreatedDirectories.OrderByDescending(path => path.Length))
            {
                try
                {
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public void ResetPreparedDestination()
        {
            CloseWriteStreams();
            DeletePartials();
            Destinations.Clear();
            PartialPaths.Clear();
            ReceivedOffsets.Clear();
            ReceivedNextIndexes.Clear();
            CreatedDirectories.Clear();
            DestinationRoot = null;
            CollisionPolicy = TransferCollisionPolicy.Ask;
            Interlocked.Exchange(ref _transferredBytes, 0);
        }

        private static TaskCompletionSource CompletedGate()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
