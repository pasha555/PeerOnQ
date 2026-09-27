using System.Diagnostics;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class Phase4CollaborationTests
{
    private static readonly PeerOnQId Device = PeerOnQId.Parse("LNK-483-921-756-204");

    [Fact]
    public void Protocol_round_trips_binary_chunks_without_base64_expansion()
    {
        var payload = Enumerable.Range(0, 1024).Select(index => (byte)(index % 251)).ToArray();
        var message = new TransferChunk
        {
            SessionId = Guid.NewGuid(),
            PermissionGeneration = 1,
            TransferId = Guid.NewGuid(),
            RelativePath = "folder/file.bin",
            Offset = 4096,
            Index = 4,
            ChunkSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
            Payload = payload,
        };

        var encoded = CollaborationProtocolCodec.Encode(message);
        Assert.True(encoded.Length < payload.Length + 512);
        Assert.True(CollaborationProtocolCodec.TryDecode(encoded, out var decoded, out var error), error);
        var chunk = Assert.IsType<TransferChunk>(decoded);
        Assert.Equal(payload, chunk.Payload);
        Assert.Equal(message.Offset, chunk.Offset);
    }

    [Fact]
    public async Task Media_transport_rejects_application_data_until_hybrid_handshake_is_secure()
    {
        var sessionId = SessionId.New();
        var media = new FakeMediaSession(sessionId, SessionRole.Viewer);
        media.SetDataChannelReady();
        await using var transport = new MediaCollaborationTransport(
            media,
            SessionPermission.FileTransfer | SessionPermission.ControlInput | SessionPermission.ViewScreen,
            new RejectingHybridIdentityProvider(),
            new string('a', 64));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(new TransferPause { TransferId = Guid.NewGuid() }));
        Assert.False(transport.IsReady);
    }

    [Fact]
    public async Task Collaboration_protocol_error_pauses_clipboard_fail_closed()
    {
        var sessionId = SessionId.New();
        var permissions = SessionPermission.FileTransfer | SessionPermission.ClipboardText;
        var (localTransport, remoteTransport) = InMemoryTransport.CreatePair(permissions);
        var localClipboard = new FakeClipboard();
        var remoteClipboard = new FakeClipboard();
        var files = new FileTransferService(localTransport);
        var clipboard = new ClipboardSyncService(localTransport, localClipboard);
        await using var remote = new ClipboardSyncService(remoteTransport, remoteClipboard);
        await using var context = new SessionCollaborationContext(
            sessionId,
            permissions,
            localTransport,
            files,
            clipboard,
            remoteInput: null);
        await clipboard.EnableAsync(userConfirmed: true);
        await remote.EnableAsync(userConfirmed: true);
        string? error = null;
        context.ProtocolError += (_, reason) => error = reason;

        localTransport.Reject("invalid_binding");

        Assert.True(clipboard.IsPaused);
        Assert.Equal("invalid_binding", error);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/../../escape.txt")]
    [InlineData("C:/Windows/file.txt")]
    [InlineData("CON.txt")]
    [InlineData("folder/name. ")]
    public void Unsafe_transfer_paths_are_rejected(string path) =>
        Assert.ThrowsAny<Exception>(() => SafeTransferPath.NormalizeRelative(path));

    [Fact]
    public async Task Destination_junction_escape_is_rejected_on_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var junction = Path.Combine(root.Path, "junction");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", junction, outside.Path },
        }) ?? throw new InvalidOperationException("Could not start the Windows junction probe.");
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                SafeTransferPath.ResolveUnderRoot(root.Path, Path.Combine("junction", "escape.bin")));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task File_transfer_writes_partial_verifies_hash_and_atomically_completes()
    {
        using var sourceRoot = new TemporaryDirectory();
        using var destinationRoot = new TemporaryDirectory();
        var source = Path.Combine(sourceRoot.Path, "payload.bin");
        var expected = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 239)).ToArray();
        await File.WriteAllBytesAsync(source, expected);

        var (leftTransport, rightTransport) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var sender = new FileTransferService(leftTransport);
        await using var receiver = new FileTransferService(rightTransport);
        var senderCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiverCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.IncomingOffer += async (_, offer) =>
            await receiver.AcceptAsync(offer.TransferId, destinationRoot.Path, TransferCollisionPolicy.Rename);
        sender.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.Status == TransferStatus.Completed) senderCompleted.TrySetResult();
        };
        receiver.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.Status == TransferStatus.Completed) receiverCompleted.TrySetResult();
        };

        await sender.OfferAsync([source]);
        await Task.WhenAll(
            senderCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            receiverCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var received = Path.Combine(destinationRoot.Path, "payload.bin");
        Assert.Equal(expected, await File.ReadAllBytesAsync(received));
        Assert.Empty(Directory.EnumerateFiles(destinationRoot.Path, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Interrupted_file_transfer_resumes_from_the_receivers_partial_offset()
    {
        using var sourceRoot = new TemporaryDirectory();
        using var destinationRoot = new TemporaryDirectory();
        var source = Path.Combine(sourceRoot.Path, "resume.bin");
        var expected = Enumerable.Range(0, 4 * 1024 * 1024).Select(index => (byte)(index % 239)).ToArray();
        await File.WriteAllBytesAsync(source, expected);

        var (senderTransport, receiverTransport) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        senderTransport.SendDelay = TimeSpan.FromMilliseconds(10);
        await using var sender = new FileTransferService(senderTransport);
        await using var receiver = new FileTransferService(receiverTransport);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptionRaised = 0;

        receiver.IncomingOffer += async (_, offer) =>
            await receiver.AcceptAsync(offer.TransferId, destinationRoot.Path, TransferCollisionPolicy.Rename);
        receiver.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.Status == TransferStatus.Transferring
                && snapshot.TransferredBytes > 0
                && Interlocked.Exchange(ref interruptionRaised, 1) == 0)
            {
                sender.ConnectionInterrupted();
                receiver.ConnectionInterrupted();
                interrupted.TrySetResult();
            }

            if (snapshot.Status == TransferStatus.Completed) completed.TrySetResult();
        };

        await sender.OfferAsync([source]);
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Contains(sender.History, item => item.Status == TransferStatus.Paused);
        Assert.Contains(receiver.History, item => item.Status == TransferStatus.Paused);

        receiver.ConnectionResumed();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(destinationRoot.Path, "resume.bin")));
        Assert.Empty(Directory.EnumerateFiles(destinationRoot.Path, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Large_transfer_is_streamed_in_protocol_bounded_chunks()
    {
        using var sourceRoot = new TemporaryDirectory();
        using var destinationRoot = new TemporaryDirectory();
        var source = Path.Combine(sourceRoot.Path, "large.bin");
        await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(16L * 1024 * 1024);

        var (senderTransport, receiverTransport) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var sender = new FileTransferService(senderTransport);
        await using var receiver = new FileTransferService(receiverTransport);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.IncomingOffer += async (_, offer) =>
            await receiver.AcceptAsync(offer.TransferId, destinationRoot.Path, TransferCollisionPolicy.Rename);
        receiver.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.Status == TransferStatus.Completed) completed.TrySetResult();
        };

        await sender.OfferAsync([source]);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var chunks = senderTransport.SentMessages.OfType<TransferChunk>().ToArray();
        Assert.Equal(64, chunks.Length);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Payload.Length, 1, CollaborationProtocolCodec.MaximumChunkBytes));
        Assert.Equal(16L * 1024 * 1024, receiverTransport.DeliveryReports.Sum(report => report.DeliveredBytes));
        Assert.Contains(receiverTransport.DeliveryReports, report => report.Flush);
        Assert.Equal(16L * 1024 * 1024, new FileInfo(Path.Combine(destinationRoot.Path, "large.bin")).Length);
    }

    [Fact]
    public async Task Independent_transfers_use_bounded_parallel_workers_without_corruption()
    {
        using var sourceRoot = new TemporaryDirectory();
        using var destinationRoot = new TemporaryDirectory();
        var firstBytes = Enumerable.Range(0, 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
        var secondBytes = Enumerable.Range(0, 1024 * 1024).Select(index => (byte)(index % 239)).ToArray();
        var first = Path.Combine(sourceRoot.Path, "first.bin");
        var second = Path.Combine(sourceRoot.Path, "second.bin");
        await File.WriteAllBytesAsync(first, firstBytes);
        await File.WriteAllBytesAsync(second, secondBytes);

        var (senderTransport, receiverTransport) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        senderTransport.SendDelay = TimeSpan.FromMilliseconds(2);
        await using var sender = new FileTransferService(senderTransport, options: new FileTransferOptions
        {
            MaximumParallelTransfers = 2,
        });
        await using var receiver = new FileTransferService(receiverTransport);
        var senderCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiverCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateGate = new object();
        var senderDone = new HashSet<Guid>();
        var receiverDone = new HashSet<Guid>();
        var pendingOffers = new List<TransferOffer>();
        var bothOffersReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransferSends = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workersOverlapped = false;

        receiver.IncomingOffer += (_, offer) =>
        {
            lock (stateGate)
            {
                pendingOffers.Add(offer);
                if (pendingOffers.Count == 2) bothOffersReceived.TrySetResult();
            }
        };
        sender.TransferChanged += (_, snapshot) =>
        {
            lock (stateGate)
            {
                if (snapshot.Status == TransferStatus.Completed)
                {
                    senderDone.Add(snapshot.TransferId);
                    if (senderDone.Count == 2) senderCompleted.TrySetResult();
                }
            }
        };
        receiver.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.Status != TransferStatus.Completed) return;
            lock (stateGate)
            {
                receiverDone.Add(snapshot.TransferId);
                if (receiverDone.Count == 2) receiverCompleted.TrySetResult();
            }
        };

        await Task.WhenAll(sender.OfferAsync([first]), sender.OfferAsync([second]));
        await bothOffersReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        TransferOffer[] offers;
        lock (stateGate) offers = pendingOffers.ToArray();
        senderTransport.SendBlocker = releaseTransferSends.Task;
        try
        {
            await Task.WhenAll(offers.Select(offer => receiver.AcceptAsync(
                offer.TransferId,
                destinationRoot.Path,
                TransferCollisionPolicy.Rename)));
            var activeDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < activeDeadline)
            {
                workersOverlapped = sender.History.Count(item => item.Status == TransferStatus.Transferring) == 2;
                if (workersOverlapped) break;
                await Task.Delay(10);
            }
        }
        finally
        {
            releaseTransferSends.TrySetResult();
        }
        await Task.WhenAll(
            senderCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15)),
            receiverCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.True(workersOverlapped, "The configured transfer workers never overlapped.");
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(Path.Combine(destinationRoot.Path, "first.bin")));
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(Path.Combine(destinationRoot.Path, "second.bin")));
    }

    [Fact]
    public async Task Default_policy_has_no_product_level_twenty_gigabyte_limit()
    {
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var receiver = new FileTransferService(local);
        var offered = new TaskCompletionSource<TransferOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.IncomingOffer += (_, offer) => offered.TrySetResult(offer);
        var size = 21L * 1024 * 1024 * 1024;
        await remote.SendAsync(new TransferOffer
        {
            TransferId = Guid.NewGuid(),
            DisplayName = "large.bin",
            TotalBytes = size,
            Entries =
            [
                new TransferEntry
                {
                    RelativePath = "large.bin",
                    Kind = TransferItemKind.File,
                    Size = size,
                    Sha256 = new string('0', 64),
                },
            ],
        });

        Assert.Equal(size, (await offered.Task.WaitAsync(TimeSpan.FromSeconds(3))).TotalBytes);
    }

    [Fact]
    public async Task Nested_folder_empty_file_and_root_collision_keep_the_accepted_structure()
    {
        using var sourceParent = new TemporaryDirectory();
        using var destination = new TemporaryDirectory();
        var folder = Path.Combine(sourceParent.Path, "Project");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "empty.txt"), []);
        await File.WriteAllTextAsync(Path.Combine(folder, "nested", "note.txt"), "nested content");
        Directory.CreateDirectory(Path.Combine(destination.Path, "Project"));

        var (left, right) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var sender = new FileTransferService(left);
        await using var receiver = new FileTransferService(right);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.IncomingOffer += async (_, offer) =>
            await receiver.AcceptAsync(offer.TransferId, destination.Path, TransferCollisionPolicy.Rename);
        receiver.TransferChanged += (_, transfer) =>
        {
            if (transfer.Status == TransferStatus.Completed) completed.TrySetResult();
        };

        await sender.OfferAsync([folder]);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var renamed = Path.Combine(destination.Path, "Project (1)");
        Assert.True(File.Exists(Path.Combine(renamed, "empty.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(renamed, "empty.txt")).Length);
        Assert.Equal("nested content", await File.ReadAllTextAsync(Path.Combine(renamed, "nested", "note.txt")));
    }

    [Fact]
    public async Task Skip_collision_checkpoint_prevents_the_existing_file_from_crossing_the_network()
    {
        using var sourceRoot = new TemporaryDirectory();
        using var destination = new TemporaryDirectory();
        var source = Path.Combine(sourceRoot.Path, "existing.txt");
        var target = Path.Combine(destination.Path, "existing.txt");
        await File.WriteAllTextAsync(source, "new remote content");
        await File.WriteAllTextAsync(target, "keep local content");
        var (senderTransport, receiverTransport) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var sender = new FileTransferService(senderTransport);
        await using var receiver = new FileTransferService(receiverTransport);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.IncomingOffer += async (_, offer) =>
            await receiver.AcceptAsync(offer.TransferId, destination.Path, TransferCollisionPolicy.Skip);
        receiver.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.Status == TransferStatus.Completed) completed.TrySetResult();
        };

        await sender.OfferAsync([source]);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("keep local content", await File.ReadAllTextAsync(target));
        Assert.Empty(senderTransport.SentMessages.OfType<TransferChunk>());
    }

    [Fact]
    public async Task Cancel_removes_partial_file_and_never_exposes_an_incomplete_final_name()
    {
        using var destination = new TemporaryDirectory();
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var receiver = new FileTransferService(local);
        var transferId = Guid.NewGuid();
        var payload = "abc"u8.ToArray();
        receiver.IncomingOffer += async (_, offer) =>
            await receiver.AcceptAsync(offer.TransferId, destination.Path, TransferCollisionPolicy.Rename);
        await remote.SendAsync(new TransferOffer
        {
            TransferId = transferId,
            DisplayName = "result.txt",
            TotalBytes = payload.Length,
            Entries =
            [
                new TransferEntry
                {
                    RelativePath = "result.txt",
                    Kind = TransferItemKind.File,
                    Size = payload.Length,
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
                },
            ],
        });
        await WaitUntilAsync(() => receiver.History.Single().Status == TransferStatus.Queued);
        await remote.SendAsync(new TransferChunk
        {
            TransferId = transferId,
            RelativePath = "result.txt",
            Offset = 0,
            Index = 0,
            ChunkSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
            Payload = payload,
        });
        Assert.Single(Directory.EnumerateFiles(destination.Path, "*.partial"));

        await remote.SendAsync(new TransferCancel { TransferId = transferId });
        await WaitUntilAsync(() => receiver.History.Single().Status == TransferStatus.Canceled);
        Assert.Empty(Directory.EnumerateFiles(destination.Path, "*.partial"));
        Assert.False(File.Exists(Path.Combine(destination.Path, "result.txt")));
    }

    [Fact]
    public async Task Duplicate_or_out_of_order_chunk_fails_closed_and_removes_the_partial()
    {
        using var destination = new TemporaryDirectory();
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var receiver = new FileTransferService(local);
        var transferId = Guid.NewGuid();
        var payload = "checkpoint"u8.ToArray();
        receiver.IncomingOffer += async (_, offer) =>
            await receiver.AcceptAsync(offer.TransferId, destination.Path, TransferCollisionPolicy.Rename);
        await remote.SendAsync(CreateSingleFileOffer(transferId, "checkpoint.bin", payload));
        await WaitUntilAsync(() => receiver.History.Single().Status == TransferStatus.Queued);

        await remote.SendAsync(new TransferChunk
        {
            TransferId = transferId,
            RelativePath = "checkpoint.bin",
            Offset = 0,
            Index = 1,
            ChunkSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
            Payload = payload,
        });

        await WaitUntilAsync(() => receiver.History.Single().Status == TransferStatus.Failed);
        Assert.Equal("validation_failed", receiver.History.Single().FailureCode);
        Assert.Empty(Directory.EnumerateFiles(destination.Path, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Corrupted_chunk_fails_integrity_check_and_removes_the_partial()
    {
        using var destination = new TemporaryDirectory();
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var receiver = new FileTransferService(local);
        var transferId = Guid.NewGuid();
        var expected = "expected"u8.ToArray();
        receiver.IncomingOffer += async (_, offer) =>
            await receiver.AcceptAsync(offer.TransferId, destination.Path, TransferCollisionPolicy.Rename);
        await remote.SendAsync(CreateSingleFileOffer(transferId, "integrity.bin", expected));
        await WaitUntilAsync(() => receiver.History.Single().Status == TransferStatus.Queued);

        await remote.SendAsync(new TransferChunk
        {
            TransferId = transferId,
            RelativePath = "integrity.bin",
            Offset = 0,
            Index = 0,
            ChunkSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)).ToLowerInvariant(),
            Payload = "tampered"u8.ToArray(),
        });

        await WaitUntilAsync(() => receiver.History.Single().Status == TransferStatus.Failed);
        Assert.Equal("validation_failed", receiver.History.Single().FailureCode);
        Assert.Empty(Directory.EnumerateFiles(destination.Path, "*.partial", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(destination.Path, "integrity.bin")));
    }

    [Fact]
    public async Task Removed_destination_fails_without_recreating_or_exposing_a_final_file()
    {
        using var destinationParent = new TemporaryDirectory();
        var destination = Path.Combine(destinationParent.Path, "removed");
        Directory.CreateDirectory(destination);
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var receiver = new FileTransferService(local);
        var transferId = Guid.NewGuid();
        var payload = "destination"u8.ToArray();
        receiver.IncomingOffer += async (_, offer) =>
            await receiver.AcceptAsync(offer.TransferId, destination, TransferCollisionPolicy.Rename);
        await remote.SendAsync(CreateSingleFileOffer(transferId, "destination.bin", payload));
        await WaitUntilAsync(() => receiver.History.Single().Status == TransferStatus.Queued);
        Directory.Delete(destination, recursive: true);

        await remote.SendAsync(new TransferChunk
        {
            TransferId = transferId,
            RelativePath = "destination.bin",
            Offset = 0,
            Index = 0,
            ChunkSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
            Payload = payload,
        });

        await WaitUntilAsync(() => receiver.History.Single().Status == TransferStatus.Failed);
        Assert.Equal("destination_unavailable", receiver.History.Single().FailureCode);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Disk_space_preflight_rejects_an_offer_larger_than_available_space_before_writing()
    {
        using var destination = new TemporaryDirectory();
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var receiver = new FileTransferService(local);
        var offered = new TaskCompletionSource<TransferOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.IncomingOffer += (_, offer) => offered.TrySetResult(offer);
        var available = new DriveInfo(Path.GetPathRoot(destination.Path)!).AvailableFreeSpace;
        var size = available == long.MaxValue ? long.MaxValue : available + 1;
        await remote.SendAsync(new TransferOffer
        {
            TransferId = Guid.NewGuid(),
            DisplayName = "disk-full.bin",
            TotalBytes = size,
            Entries =
            [
                new TransferEntry
                {
                    RelativePath = "disk-full.bin",
                    Kind = TransferItemKind.File,
                    Size = size,
                    Sha256 = new string('0', 64),
                },
            ],
        });

        var offer = await offered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<IOException>(() => receiver.AcceptAsync(
            offer.TransferId,
            destination.Path,
            TransferCollisionPolicy.Rename));
        Assert.Empty(Directory.EnumerateFiles(destination.Path, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Failed_destination_preparation_cleans_runtime_state_and_can_be_retried()
    {
        using var destination = new TemporaryDirectory();
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var receiver = new FileTransferService(local);
        var transferId = Guid.NewGuid();
        var offered = new TaskCompletionSource<TransferOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.IncomingOffer += (_, offer) => offered.TrySetResult(offer);
        var payload = "retry"u8.ToArray();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        await remote.SendAsync(new TransferOffer
        {
            TransferId = transferId,
            DisplayName = "two files",
            TotalBytes = payload.Length * 2,
            Entries =
            [
                new TransferEntry { RelativePath = "first.bin", Kind = TransferItemKind.File, Size = payload.Length, Sha256 = digest },
                new TransferEntry { RelativePath = "second.bin", Kind = TransferItemKind.File, Size = payload.Length, Sha256 = digest },
            ],
        });
        await offered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var blocker = Path.Combine(destination.Path, $"second.bin.peeronq.{transferId:N}.partial");
        await File.WriteAllTextAsync(blocker, "occupied");

        await Assert.ThrowsAsync<IOException>(() => receiver.AcceptAsync(
            transferId, destination.Path, TransferCollisionPolicy.Overwrite));
        Assert.False(File.Exists(Path.Combine(destination.Path, $"first.bin.peeronq.{transferId:N}.partial")));
        Assert.True(File.Exists(blocker));

        File.Delete(blocker);
        await receiver.AcceptAsync(transferId, destination.Path, TransferCollisionPolicy.Overwrite);
        Assert.Equal(TransferStatus.Queued, receiver.History.Single().Status);
        Assert.Equal(2, Directory.EnumerateFiles(destination.Path, "*.partial").Count());
    }

    [Fact]
    public async Task Permission_and_size_limits_fail_closed_before_an_offer_is_sent()
    {
        using var sourceRoot = new TemporaryDirectory();
        var source = Path.Combine(sourceRoot.Path, "too-large.bin");
        await File.WriteAllBytesAsync(source, new byte[32]);
        var (unauthorizedTransport, _) = InMemoryTransport.CreatePair(SessionPermission.ViewScreen);
        await using var unauthorized = new FileTransferService(unauthorizedTransport);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unauthorized.OfferAsync([source]));

        var (limitedTransport, _) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        await using var limited = new FileTransferService(
            limitedTransport,
            new FileTransferOptions { MaximumFileBytes = 16, MaximumTransferBytes = 16 });
        await Assert.ThrowsAsync<InvalidDataException>(() => limited.OfferAsync([source]));
        Assert.Empty(limitedTransport.SentMessages);
    }

    [Fact]
    public async Task Clipboard_is_disabled_until_confirmed_and_prevents_echo_loops()
    {
        var (leftTransport, rightTransport) = InMemoryTransport.CreatePair(SessionPermission.ClipboardText);
        var leftClipboard = new FakeClipboard();
        var rightClipboard = new FakeClipboard();
        await using var left = new ClipboardSyncService(leftTransport, leftClipboard);
        await using var right = new ClipboardSyncService(rightTransport, rightClipboard);

        leftClipboard.SetLocal("must not leave");
        await Task.Delay(50);
        Assert.Null(rightClipboard.Text);

        await left.EnableAsync(userConfirmed: true);
        await right.EnableAsync(userConfirmed: true);
        leftClipboard.SetLocal("hello over DTLS data channel");

        await WaitUntilAsync(() => rightClipboard.Text == "hello over DTLS data channel");
        Assert.Equal(1, rightClipboard.RemoteWrites);
        Assert.InRange(leftTransport.SentMessages.Count(message => message is ClipboardTextUpdate), 1, 1);
        Assert.Empty(rightTransport.SentMessages.OfType<ClipboardTextUpdate>());
    }

    [Fact]
    public async Task Clipboard_rejects_oversized_content_without_audit_content()
    {
        var (leftTransport, rightTransport) = InMemoryTransport.CreatePair(SessionPermission.ClipboardText);
        var clipboard = new FakeClipboard();
        var remoteClipboard = new FakeClipboard();
        var audit = new MemoryAudit();
        await using var service = new ClipboardSyncService(
            leftTransport,
            clipboard,
            new ClipboardSyncOptions { MaximumUtf8Bytes = 8 },
            audit);
        await using var remote = new ClipboardSyncService(rightTransport, remoteClipboard);
        await service.EnableAsync(userConfirmed: true);
        await remote.EnableAsync(userConfirmed: true);

        clipboard.SetLocal("this is too large");
        await WaitUntilAsync(() => audit.Events.Any(item => item.EventType == SecurityAuditEventType.ClipboardRejected));
        Assert.DoesNotContain(audit.Events, item => item.SafeMetadata.Values.Any(value => value.Contains("too large")));
        Assert.Empty(leftTransport.SentMessages.OfType<ClipboardTextUpdate>());
    }

    [Fact]
    public async Task Clipboard_peer_disable_revokes_delivery_without_leaking_later_content()
    {
        var (leftTransport, rightTransport) = InMemoryTransport.CreatePair(SessionPermission.ClipboardText);
        var leftClipboard = new FakeClipboard();
        var rightClipboard = new FakeClipboard();
        await using var left = new ClipboardSyncService(leftTransport, leftClipboard);
        await using var right = new ClipboardSyncService(rightTransport, rightClipboard);
        await left.EnableAsync(userConfirmed: true);
        await right.EnableAsync(userConfirmed: true);
        leftClipboard.SetLocal("first");
        await WaitUntilAsync(() => rightClipboard.Text == "first");
        var sentBeforeDisable = leftTransport.SentMessages.OfType<ClipboardTextUpdate>().Count();

        await right.DisableAsync();
        Assert.False(left.IsRemoteEnabled);
        leftClipboard.SetLocal("must not cross after revoke");
        await Task.Delay(50);

        Assert.Null(rightClipboard.Text);
        Assert.Equal(sentBeforeDisable, leftTransport.SentMessages.OfType<ClipboardTextUpdate>().Count());
    }

    [Fact]
    public async Task Fingerprint_change_revokes_trust_and_permissions_cannot_expand()
    {
        var store = new MemoryProfileStore();
        var audit = new MemoryAudit();
        var service = new TrustedDeviceService(store, audit);
        var fingerprint = new string('a', 64);
        await service.ApproveAsync(
            Device,
            "Office PC",
            fingerprint,
            SessionPermission.ViewScreen,
            DateTimeOffset.UtcNow.AddDays(30),
            localUserConfirmed: true);

        Assert.NotNull(await service.ValidateAsync(Device, fingerprint, SessionPermission.ViewScreen));
        Assert.Null(await service.ValidateAsync(Device, fingerprint, SessionPermission.ViewScreen | SessionPermission.FileTransfer));
        Assert.Contains(audit.Events, item => item.Outcome == "scope_expansion_rejected");
        Assert.Null(await service.ValidateAsync(Device, new string('b', 64), SessionPermission.ViewScreen));
        Assert.Equal(DeviceTrustState.FingerprintChanged, (await service.ListAsync()).Single().TrustState);
    }

    [Fact]
    public async Task Unattended_access_defaults_off_enforces_strength_lockout_and_recovery_is_single_use()
    {
        var store = new MemoryProfileStore();
        var trusted = new TrustedDeviceService(store);
        var audit = new MemoryAudit();
        var service = new UnattendedAccessService(store, trusted, audit);
        Assert.False(await service.IsEnabledAsync());

        await Assert.ThrowsAsync<ArgumentException>(() => service.EnableAsync(new UnattendedSetupRequest
        {
            LocalUserConfirmed = true,
            ExplanationAcknowledged = true,
            StrongPassword = "weak",
        }));

        var setup = await service.EnableAsync(new UnattendedSetupRequest
        {
            LocalUserConfirmed = true,
            ExplanationAcknowledged = true,
            StrongPassword = "Correct-Horse-9!Battery",
            AllowedPermissions = SessionPermission.ViewScreen,
        });
        Assert.Equal(10, setup.RecoveryCodes.Count);
        Assert.Equal(UnattendedAuthenticationResult.PermissionDenied,
            await service.AuthenticatePasswordAsync(Device, "Correct-Horse-9!Battery", SessionPermission.FileTransfer));

        var recovery = setup.RecoveryCodes[0];
        Assert.Equal(UnattendedAuthenticationResult.Succeeded,
            await service.AuthenticatePasswordAsync(Device, recovery, SessionPermission.ViewScreen));
        Assert.Equal(UnattendedAuthenticationResult.InvalidCredential,
            await service.AuthenticatePasswordAsync(Device, recovery, SessionPermission.ViewScreen));

        for (var attempt = 0; attempt < 4; attempt++)
            await service.AuthenticatePasswordAsync(Device, "wrong credential", SessionPermission.ViewScreen);
        Assert.Equal(UnattendedAuthenticationResult.LockedOut,
            await service.AuthenticatePasswordAsync(Device, "wrong credential", SessionPermission.ViewScreen));
        Assert.Contains(audit.Events, item => item.EventType == SecurityAuditEventType.UnattendedLockedOut);
    }

    [Fact]
    public async Task Trusted_device_unattended_setting_reports_scope_and_disable_revokes_credential_paths()
    {
        var store = new MemoryProfileStore();
        var trusted = new TrustedDeviceService(store);
        var service = new UnattendedAccessService(store, trusted);
        var fingerprint = new string('a', 64);
        var fullControl = SessionPermissionPolicy.ForMode(SessionMode.FullControl);
        await trusted.ApproveAsync(
            Device,
            "Approved laptop",
            fingerprint,
            fullControl,
            DateTimeOffset.UtcNow.AddDays(30),
            localUserConfirmed: true);

        await service.EnableAsync(new UnattendedSetupRequest
        {
            LocalUserConfirmed = true,
            ExplanationAcknowledged = true,
            DeviceAuthenticationEnabled = true,
            AllowedPermissions = fullControl,
        });

        var enabled = await service.GetStatusAsync();
        Assert.True(enabled.Enabled);
        Assert.False(enabled.PasswordConfigured);
        Assert.True(enabled.TrustedDeviceAuthenticationEnabled);
        Assert.Equal(fullControl, enabled.AllowedPermissions);
        Assert.Equal(
            UnattendedAuthenticationResult.Succeeded,
            await service.AuthenticateTrustedDeviceAsync(Device, fingerprint, fullControl));
        Assert.Equal(
            UnattendedAuthenticationResult.DeviceNotTrusted,
            await service.AuthenticateTrustedDeviceAsync(Device, new string('b', 64), fullControl));

        await service.DisableAsync(localUserConfirmed: true);

        var disabled = await service.GetStatusAsync();
        Assert.False(disabled.Enabled);
        Assert.False(disabled.PasswordConfigured);
        Assert.False(disabled.TrustedDeviceAuthenticationEnabled);
        Assert.Equal(
            UnattendedAuthenticationResult.Disabled,
            await service.AuthenticateTrustedDeviceAsync(Device, fingerprint, fullControl));
    }

    [Fact]
    public async Task Unattended_password_challenge_is_short_lived_bound_to_identity_scope_and_single_use()
    {
        var store = new MemoryProfileStore();
        var trusted = new TrustedDeviceService(store);
        var service = new UnattendedAccessService(store, trusted);
        const string password = "Correct-Horse-9!Battery"; // secret-scan: allow-test-vector
        const string fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await service.EnableAsync(new UnattendedSetupRequest
        {
            LocalUserConfirmed = true,
            ExplanationAcknowledged = true,
            StrongPassword = password,
            AllowedPermissions = SessionPermission.ViewScreen,
        });

        var challenge = await service.IssuePasswordChallengeAsync(new UnattendedChallengeRequestNotification(
            "request-1", Device, fingerprint, DateTimeOffset.UtcNow.AddSeconds(30)));
        Assert.Equal(SessionPermission.ViewScreen, challenge.AllowedPermissions);
        var proof = UnattendedAccessService.CreatePasswordProof(
            password, challenge, Device, fingerprint, SessionPermission.ViewScreen);

        Assert.Equal(UnattendedAuthenticationResult.Succeeded,
            await service.VerifyPasswordProofAsync(
                Device,
                fingerprint,
                SessionPermission.ViewScreen,
                challenge.ChallengeId!.Value,
                proof));
        Assert.Equal(UnattendedAuthenticationResult.InvalidCredential,
            await service.VerifyPasswordProofAsync(
                Device,
                fingerprint,
                SessionPermission.ViewScreen,
                challenge.ChallengeId.Value,
                proof));

        var second = await service.IssuePasswordChallengeAsync(new UnattendedChallengeRequestNotification(
            "request-2", Device, fingerprint, DateTimeOffset.UtcNow.AddSeconds(30)));
        var scopeTamperedProof = UnattendedAccessService.CreatePasswordProof(
            password, second, Device, fingerprint, SessionPermission.ViewScreen);
        Assert.Equal(UnattendedAuthenticationResult.PermissionDenied,
            await service.VerifyPasswordProofAsync(
                Device,
                fingerprint,
                SessionPermission.FileTransfer,
                second.ChallengeId!.Value,
                scopeTamperedProof));
    }

    [Fact]
    public async Task Unattended_recovery_code_uses_a_one_time_challenge_proof_without_sending_the_code()
    {
        var store = new MemoryProfileStore();
        var audit = new MemoryAudit();
        var service = new UnattendedAccessService(store, new TrustedDeviceService(store), audit);
        var setup = await service.EnableAsync(new UnattendedSetupRequest
        {
            LocalUserConfirmed = true,
            ExplanationAcknowledged = true,
            DeviceAuthenticationEnabled = true,
            AllowedPermissions = SessionPermission.ViewScreen,
        });
        var recoveryCode = setup.RecoveryCodes[0];
        var fingerprint = new string('c', 64);
        var challenge = await service.IssuePasswordChallengeAsync(new UnattendedChallengeRequestNotification(
            "recovery-1", Device, fingerprint, DateTimeOffset.UtcNow.AddSeconds(30)));
        var proof = UnattendedAccessService.CreatePasswordProof(
            recoveryCode,
            challenge,
            Device,
            fingerprint,
            SessionPermission.ViewScreen);
        Assert.DoesNotContain(recoveryCode, proof, StringComparison.Ordinal);
        Assert.Equal(UnattendedAuthenticationResult.Succeeded,
            await service.VerifyPasswordProofAsync(
                Device,
                fingerprint,
                SessionPermission.ViewScreen,
                challenge.ChallengeId!.Value,
                proof));
        Assert.DoesNotContain(audit.Events, item =>
            item.SafeMetadata.Values.Any(value => value.Contains(recoveryCode, StringComparison.Ordinal))
            || (item.Outcome?.Contains(recoveryCode, StringComparison.Ordinal) ?? false));

        var replayChallenge = await service.IssuePasswordChallengeAsync(new UnattendedChallengeRequestNotification(
            "recovery-2", Device, fingerprint, DateTimeOffset.UtcNow.AddSeconds(30)));
        var replayProof = UnattendedAccessService.CreatePasswordProof(
            recoveryCode,
            replayChallenge,
            Device,
            fingerprint,
            SessionPermission.ViewScreen);
        Assert.Equal(UnattendedAuthenticationResult.InvalidCredential,
            await service.VerifyPasswordProofAsync(
                Device,
                fingerprint,
                SessionPermission.ViewScreen,
                replayChallenge.ChallengeId!.Value,
                replayProof));
    }

    [Fact]
    public async Task Unattended_challenge_proof_failures_trigger_the_persisted_lockout()
    {
        var store = new MemoryProfileStore();
        var audit = new MemoryAudit();
        var service = new UnattendedAccessService(store, new TrustedDeviceService(store), audit);
        await service.EnableAsync(new UnattendedSetupRequest
        {
            LocalUserConfirmed = true,
            ExplanationAcknowledged = true,
            StrongPassword = "Correct-Horse-9!Battery",
            AllowedPermissions = SessionPermission.ViewScreen,
        });
        var fingerprint = new string('d', 64);
        UnattendedAuthenticationResult result = UnattendedAuthenticationResult.Succeeded;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var challenge = await service.IssuePasswordChallengeAsync(new UnattendedChallengeRequestNotification(
                $"lockout-{attempt}", Device, fingerprint, DateTimeOffset.UtcNow.AddSeconds(30)));
            result = await service.VerifyPasswordProofAsync(
                Device,
                fingerprint,
                SessionPermission.ViewScreen,
                challenge.ChallengeId!.Value,
                "not-base64");
        }

        Assert.Equal(UnattendedAuthenticationResult.LockedOut, result);
        Assert.NotNull(store.Snapshot.UnattendedAccess.LockedUntil);
        Assert.Contains(audit.Events, item => item.EventType == SecurityAuditEventType.UnattendedLockedOut);
    }

    [Fact]
    public async Task Address_book_supports_groups_tags_favorites_search_and_real_presence_provider()
    {
        var store = new MemoryProfileStore();
        var presence = new FixedPresenceProvider(DevicePresence.Online);
        var service = new AddressBookService(store, presence);
        var group = await service.SaveGroupAsync(new AddressBookGroup { GroupId = Guid.Empty, Name = "Office" });
        var saved = await service.SaveDeviceAsync(new AddressBookDevice
        {
            RecordId = Guid.Empty,
            DeviceId = Device,
            DisplayName = "Workstation",
            OperatingSystem = "  Windows\t",
            Tags = ["production", "windows"],
            Notes = "Finance floor",
            IsFavorite = true,
            GroupIds = [group.GroupId],
        });

        var result = await service.SearchAsync("finance", group.GroupId, favoritesOnly: true);
        var item = Assert.Single(result);
        Assert.Equal(DevicePresence.Online, item.Presence);
        Assert.Equal("Workstation", item.Device.DisplayName);
        Assert.Null(saved.OperatingSystem);
        Assert.Single(await service.SearchAsync("windows"));
        Assert.NotNull(item.Device.LastSeenVerifiedAt);

        await service.RemoveGroupAsync(group.GroupId);
        Assert.Empty(await service.ListGroupsAsync());
        Assert.Empty((await service.SearchAsync()).Single().Device.GroupIds);
        await service.RemoveDeviceAsync(saved.RecordId);
        Assert.Empty(await service.SearchAsync());
    }

    [Fact]
    public async Task Connected_devices_are_added_once_and_keep_the_user_alias()
    {
        var store = new MemoryProfileStore();
        var service = new AddressBookService(store);
        var firstConnection = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var secondConnection = firstConnection.AddMinutes(15);

        var discovered = await service.RecordConnectedDeviceAsync(Device, "Remote laptop", firstConnection);
        await service.SaveDeviceAsync(discovered with { DisplayName = "Office Laptop" });
        var reconnected = await service.RecordConnectedDeviceAsync(Device, "Renamed Windows host", secondConnection);

        var saved = Assert.Single(await service.SearchAsync());
        Assert.Equal(discovered.RecordId, reconnected.RecordId);
        Assert.Equal("Office Laptop", saved.Device.DisplayName);
        Assert.Equal(secondConnection, saved.Device.LastSeenAt);
        Assert.Equal(secondConnection, saved.Device.LastSeenVerifiedAt);
    }

    [Fact]
    public void Full_control_includes_file_transfer_but_not_text_clipboard()
    {
        var permissions = SessionPermissionPolicy.ForMode(SessionMode.FullControl);
        Assert.True(permissions.HasFlag(SessionPermission.ControlInput));
        Assert.True(permissions.HasFlag(SessionPermission.FileTransfer));
        Assert.False(permissions.HasFlag(SessionPermission.ClipboardText));
    }

    [Fact]
    public void Custom_control_requires_visible_screen_permission()
    {
        Assert.False(SessionPermissionPolicy.IsValid(
            SessionMode.Custom,
            SessionPermission.ControlInput));
        Assert.True(SessionPermissionPolicy.IsValid(
            SessionMode.Custom,
            SessionPermission.ViewScreen | SessionPermission.ControlInput));
    }

    [Fact]
    public void Input_ack_codec_round_trips_bounded_measurement_fields()
    {
        var sessionId = SessionId.New();
        var acknowledgment = new RemoteInputAcknowledgement
        {
            SessionId = sessionId.Value,
            PermissionGeneration = 1,
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionGeneration = 2,
            FocusGeneration = 3,
            Sequence = 4,
            AcknowledgedSequence = 5,
            ViewerSentAtUnixMicroseconds = 3_600_000_000,
            InjectedAtUnixMicroseconds = 3_600_032_000,
        };

        var encoded = CollaborationProtocolCodec.Encode(acknowledgment);

        Assert.True(CollaborationProtocolCodec.TryDecode(encoded, out var decoded, out var error), error);
        Assert.Equal(acknowledgment, Assert.IsType<RemoteInputAcknowledgement>(decoded));

        var legacyCommand = new RemoteInputCommand
        {
            SessionId = sessionId.Value,
            PermissionGeneration = 1,
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionGeneration = 2,
            FocusGeneration = 3,
            Sequence = 6,
            Input = new RemoteInputEvent
            {
                Kind = RemoteInputEventKind.PointerMove,
                NormalizedX = 0.5,
                NormalizedY = 0.5,
            },
        };

        var legacyEncoded = CollaborationProtocolCodec.Encode(legacyCommand);
        Assert.DoesNotContain("measurementSentAtUs", Encoding.UTF8.GetString(legacyEncoded));
    }

    [Fact]
    public async Task Negotiated_input_ack_reports_send_to_successful_injection_latency()
    {
        var sessionId = SessionId.New();
        var (viewerTransport, sharerTransport) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        viewerTransport.IsInputAcknowledgementNegotiated = true;
        sharerTransport.IsInputAcknowledgementNegotiated = true;
        viewerTransport.PeerClockEstimate = new PeerClockEstimate(
            RemoteMinusLocalOffsetMicroseconds: 25_000,
            UncertaintyMicroseconds: 1_000);
        var viewerClock = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddHours(1));
        var sharerClock = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddHours(1).AddMilliseconds(32));
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var viewer = new RemoteInputSession(
            sessionId,
            SessionRole.Viewer,
            viewerTransport,
            timeProvider: viewerClock);
        await using var sharer = new RemoteInputSession(
            sessionId,
            SessionRole.Sharer,
            sharerTransport,
            sink,
            timeProvider: sharerClock);

        await viewer.SetLocalCaptureEnabledAsync(true);
        viewerTransport.SentMessages.Clear();
        sharerTransport.SentMessages.Clear();
        await viewer.SendKeyAsync(0x41, isPressed: true, isExtendedKey: false);
        await WaitUntilAsync(() => viewerTransport.InputLatencyMeasurements.Count == 1);
        await viewer.SendKeyAsync(0x42, isPressed: true, isExtendedKey: false);

        var commands = viewerTransport.SentMessages.OfType<RemoteInputCommand>().ToArray();
        Assert.Equal(2, commands.Length);
        var command = commands[0];
        Assert.Null(commands[1].MeasurementSentAtUnixMicroseconds);
        var acknowledgment = Assert.Single(sharerTransport.SentMessages.OfType<RemoteInputAcknowledgement>());
        Assert.Equal(command.Sequence, acknowledgment.AcknowledgedSequence);
        Assert.Equal(command.MeasurementSentAtUnixMicroseconds, acknowledgment.ViewerSentAtUnixMicroseconds);
        var measurement = Assert.Single(viewerTransport.InputLatencyMeasurements);
        Assert.Equal(7, measurement.LatencyMilliseconds);
        Assert.Equal(1, measurement.ClockUncertaintyMilliseconds);
        Assert.Equal(2, sink.Inputs.Count);
    }

    [Theory]
    [InlineData(1_000_000, 1_032_000, 25_000, 1_000, 7)]
    [InlineData(1_000_000, 1_075_000, 50_000, 5_000, 25)]
    public void Input_latency_estimator_uses_remote_minus_local_clock_offset(
        long sentAt,
        long injectedAt,
        long offset,
        long uncertainty,
        double expectedMilliseconds)
    {
        Assert.True(InputLatencyEstimator.TryCalculate(
            sentAt,
            injectedAt,
            new PeerClockEstimate(offset, uncertainty),
            out var measurement));
        Assert.Equal(expectedMilliseconds, measurement.LatencyMilliseconds);
        Assert.Equal(uncertainty / 1_000d, measurement.ClockUncertaintyMilliseconds);
    }

    [Fact]
    public void Input_latency_estimator_rejects_backwards_or_uncertain_samples()
    {
        Assert.False(InputLatencyEstimator.TryCalculate(
            1_000_000,
            999_999,
            new PeerClockEstimate(0, 1_000),
            out _));
        Assert.False(InputLatencyEstimator.TryCalculate(
            1_000_000,
            1_100_000,
            new PeerClockEstimate(0, 50_001),
            out _));
    }

    [Fact]
    public async Task Input_measurement_delivery_failure_never_disables_authorized_control()
    {
        var sessionId = SessionId.New();
        var (viewerTransport, sharerTransport) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        viewerTransport.IsInputAcknowledgementNegotiated = true;
        sharerTransport.IsInputAcknowledgementNegotiated = true;
        viewerTransport.PeerClockEstimate = new PeerClockEstimate(0, 1_000);
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var viewer = new RemoteInputSession(sessionId, SessionRole.Viewer, viewerTransport);
        await using var sharer = new RemoteInputSession(sessionId, SessionRole.Sharer, sharerTransport, sink);
        var warnings = new List<string>();
        sharer.Warning += (_, warning) => warnings.Add(warning);

        await viewer.SetLocalCaptureEnabledAsync(true);
        sharerTransport.SendFailure = message => message is RemoteInputAcknowledgement
            ? new IOException("simulated_ack_failure")
            : null;
        await viewer.SendKeyAsync(0x41, isPressed: true, isExtendedKey: false);
        await WaitUntilAsync(() => warnings.Contains("input_measurement_delivery_failed"));
        await viewer.SendKeyAsync(0x42, isPressed: true, isExtendedKey: false);

        Assert.True(viewer.IsLocallyEnabled);
        Assert.Equal(2, sink.Inputs.Count);
    }

    [Fact]
    public async Task Remote_input_requires_scope_applies_ordered_commands_and_releases_held_state()
    {
        var sessionId = SessionId.New();
        var (viewerTransport, sharerTransport) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var viewer = new RemoteInputSession(sessionId, SessionRole.Viewer, viewerTransport);
        await using var sharer = new RemoteInputSession(sessionId, SessionRole.Sharer, sharerTransport, sink);

        await viewer.SetLocalCaptureEnabledAsync(true);
        await viewer.SendPointerMoveAsync(0.25, 0.75);
        await viewer.SendPointerButtonAsync(0.25, 0.75, RemotePointerButton.Left, isPressed: true);
        await viewer.SendPointerWheelAsync(0.25, 0.75, 120, isHorizontal: true);
        await viewer.SendKeyAsync(0x41, isPressed: true, isExtendedKey: false);

        Assert.Collection(
            sink.Inputs,
            input => Assert.Equal(RemoteInputEventKind.PointerMove, input.Kind),
            input => Assert.Equal(RemoteInputEventKind.PointerButton, input.Kind),
            input =>
            {
                Assert.Equal(RemoteInputEventKind.PointerWheel, input.Kind);
                Assert.True(input.IsHorizontalWheel);
            },
            input => Assert.Equal(RemoteInputEventKind.Key, input.Kind));
        Assert.All(
            viewerTransport.SentMessages.OfType<RemoteInputCommand>(),
            command => Assert.Null(command.MeasurementSentAtUnixMicroseconds));
        Assert.Empty(sharerTransport.SentMessages.OfType<RemoteInputAcknowledgement>());

        await viewer.SetLocalCaptureEnabledAsync(false);
        Assert.Equal(1, sink.ReleaseCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewer.SendKeyAsync(0x41, false, false));
    }

    [Fact]
    public async Task Concurrent_remote_input_enable_requests_share_one_focus_generation()
    {
        var sessionId = SessionId.New();
        var (viewerTransport, sharerTransport) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewerTransport.SendStarted = sendStarted;
        viewerTransport.SendBlocker = releaseSend.Task;
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var viewer = new RemoteInputSession(sessionId, SessionRole.Viewer, viewerTransport);
        await using var sharer = new RemoteInputSession(sessionId, SessionRole.Sharer, sharerTransport, sink);

        var firstEnable = viewer.SetLocalCaptureEnabledAsync(true);
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var duplicateEnable = viewer.SetLocalCaptureEnabledAsync(true);
        releaseSend.TrySetResult();

        await Task.WhenAll(firstEnable, duplicateEnable);
        await viewer.SendPointerMoveAsync(0.4, 0.6);

        var focus = Assert.Single(viewerTransport.SentMessages.OfType<RemoteInputFocusRequest>());
        var command = Assert.Single(viewerTransport.SentMessages.OfType<RemoteInputCommand>());
        Assert.Equal(focus.FocusGeneration, command.FocusGeneration);
        Assert.Single(sink.Inputs);
    }

    [Fact]
    public async Task Multi_session_focus_switch_revokes_the_previous_input_route_without_cross_session_leakage()
    {
        var firstId = SessionId.New();
        var secondId = SessionId.New();
        var (firstViewerTransport, firstSharerTransport) =
            InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var (secondViewerTransport, secondSharerTransport) =
            InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sink = new FakeRemoteInputSink();
        var focus = new RemoteInputFocusCoordinator();

        await using var firstViewer = new RemoteInputSession(
            firstId, SessionRole.Viewer, firstViewerTransport);
        await using var firstSharer = new RemoteInputSession(
            firstId, SessionRole.Sharer, firstSharerTransport, sink, focus);
        await using var secondViewer = new RemoteInputSession(
            secondId, SessionRole.Viewer, secondViewerTransport);
        await using var secondSharer = new RemoteInputSession(
            secondId, SessionRole.Sharer, secondSharerTransport, sink, focus);

        await firstViewer.SetLocalCaptureEnabledAsync(true);
        await firstViewer.SendKeyAsync(0x41, isPressed: true, isExtendedKey: false);
        Assert.True(focus.IsOwner(firstId));

        await secondViewer.SetLocalCaptureEnabledAsync(true);
        await WaitUntilAsync(() => !firstViewer.IsLocallyEnabled);
        Assert.False(focus.IsOwner(firstId));
        Assert.True(focus.IsOwner(secondId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstViewer.SendKeyAsync(0x42, isPressed: true, isExtendedKey: false));

        await secondViewer.SendKeyAsync(0x43, isPressed: true, isExtendedKey: false);

        Assert.Equal(2, sink.Inputs.Count);
        Assert.Equal((ushort)0x41, sink.Inputs[0].VirtualKey);
        Assert.Equal((ushort)0x43, sink.Inputs[1].VirtualKey);
    }

    [Fact]
    public async Task Remote_input_replay_or_invalid_coordinates_disable_injection_fail_closed()
    {
        var sessionId = SessionId.New();
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var receiver = new RemoteInputSession(sessionId, SessionRole.Sharer, local, sink);

        await remote.SendAsync(new RemoteInputFocusRequest
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionId = sessionId.Value,
            SessionGeneration = 1,
            FocusGeneration = 1,
            Sequence = 1,
            Enabled = true,
        });

        var valid = new RemoteInputCommand
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionId = sessionId.Value,
            SessionGeneration = 1,
            FocusGeneration = 1,
            Sequence = 2,
            Input = new RemoteInputEvent
            {
                Kind = RemoteInputEventKind.PointerMove,
                NormalizedX = 0.5,
                NormalizedY = 0.5,
            },
        };
        await remote.SendAsync(valid);
        await remote.SendAsync(valid);

        Assert.Single(sink.Inputs);
        Assert.Equal(1, sink.DisableCalls);

        receiver.ConnectionInterrupted();
        receiver.ConnectionResumed();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await remote.SendAsync(new RemoteInputFocusRequest
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionId = sessionId.Value,
            SessionGeneration = 2,
            FocusGeneration = 2,
            Sequence = 3,
            Enabled = true,
        });
        await remote.SendAsync(new RemoteInputCommand
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionId = sessionId.Value,
            SessionGeneration = 2,
            FocusGeneration = 2,
            Sequence = 4,
            Input = new RemoteInputEvent
            {
                Kind = RemoteInputEventKind.PointerMove,
                NormalizedX = double.NaN,
                NormalizedY = 0.5,
            },
        });
        Assert.Equal(3, sink.DisableCalls);
    }

    [Fact]
    public void Remote_input_transport_cannot_be_created_without_control_permission()
    {
        var (transport, _) = InMemoryTransport.CreatePair(SessionPermission.ViewScreen);
        Assert.Throws<UnauthorizedAccessException>(() =>
            new RemoteInputSession(SessionId.New(), SessionRole.Viewer, transport));
    }

    [Fact]
    public async Task Remote_input_permission_revocation_is_immediate_permanent_and_visible_to_viewer()
    {
        var sessionId = SessionId.New();
        var (viewerTransport, sharerTransport) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var viewer = new RemoteInputSession(sessionId, SessionRole.Viewer, viewerTransport);
        await using var sharer = new RemoteInputSession(sessionId, SessionRole.Sharer, sharerTransport, sink);
        RemoteInputPermissionStatus? status = null;
        viewer.ControlPermissionChanged += (_, value) => status = value;

        await viewer.SetLocalCaptureEnabledAsync(true);
        await viewer.SendKeyAsync(0x41, isPressed: true, isExtendedKey: false);
        await sharer.RevokeControlAsync();

        Assert.False(viewer.IsLocallyEnabled);
        Assert.False(viewer.IsControlPermissionAvailable);
        Assert.Equal("owner_revoked", status?.ReasonCode);
        Assert.True(sink.DisableCalls >= 1);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.SetLocalCaptureEnabledAsync(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewer.SendKeyAsync(0x41, false, false));
    }

    [Fact]
    public async Task Remote_input_reconnect_requires_new_session_and_focus_generations_and_rejects_stale_frames()
    {
        var sessionId = SessionId.New();
        var (viewerTransport, sharerTransport) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var viewer = new RemoteInputSession(sessionId, SessionRole.Viewer, viewerTransport);
        await using var sharer = new RemoteInputSession(sessionId, SessionRole.Sharer, sharerTransport, sink);

        await viewer.SetLocalCaptureEnabledAsync(true);
        await viewer.SendPointerMoveAsync(0.1, 0.2);
        var stale = Assert.Single(viewerTransport.SentMessages.OfType<RemoteInputCommand>());

        viewer.ConnectionInterrupted();
        sharer.ConnectionInterrupted();
        viewer.ConnectionResumed();
        sharer.ConnectionResumed();
        Assert.False(viewer.IsLocallyEnabled);

        await viewer.SetLocalCaptureEnabledAsync(true);
        await viewer.SendPointerMoveAsync(0.8, 0.9);
        Assert.Equal(2, sink.Inputs.Count);
        var current = viewerTransport.SentMessages.OfType<RemoteInputCommand>().Last();
        Assert.Equal(2, current.SessionGeneration);
        Assert.Equal(2, current.FocusGeneration);

        await viewerTransport.SendAsync(stale);
        Assert.Equal(2, sink.Inputs.Count);
        Assert.True(sink.DisableCalls >= 2);
    }

    [Fact]
    public async Task Remote_input_rejects_a_frame_bound_to_another_session()
    {
        var sessionId = SessionId.New();
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var receiver = new RemoteInputSession(sessionId, SessionRole.Sharer, local, sink);
        string? warning = null;
        receiver.Warning += (_, value) => warning = value;

        await remote.SendAsync(new RemoteInputFocusRequest
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionId = SessionId.New().Value,
            SessionGeneration = 1,
            FocusGeneration = 1,
            Sequence = 1,
            Enabled = true,
        });

        Assert.Equal("input_session_binding_rejected", warning);
        Assert.Equal(1, sink.DisableCalls);
        Assert.Empty(sink.Inputs);
    }

    [Fact]
    public async Task Remote_input_move_queue_coalesces_to_the_latest_pending_position()
    {
        var sessionId = SessionId.New();
        var (viewerTransport, sharerTransport) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var viewer = new RemoteInputSession(sessionId, SessionRole.Viewer, viewerTransport);
        await using var sharer = new RemoteInputSession(sessionId, SessionRole.Sharer, sharerTransport, sink);
        await viewer.SetLocalCaptureEnabledAsync(true);
        viewerTransport.SentMessages.Clear();
        viewerTransport.SendDelay = TimeSpan.FromMilliseconds(30);

        var pump = viewer.SendPointerMoveAsync(0.01, 0.01);
        for (var index = 2; index <= 50; index++)
            _ = viewer.SendPointerMoveAsync(index / 50d, index / 50d);
        await pump;

        var moves = viewerTransport.SentMessages.OfType<RemoteInputCommand>().ToArray();
        Assert.InRange(moves.Length, 1, 2);
        Assert.Equal(1d, moves[^1].Input.NormalizedX);
        Assert.Equal(1d, moves[^1].Input.NormalizedY);
    }

    [Fact]
    public async Task Remote_input_high_polling_pointer_burst_is_paced_without_revoking_control()
    {
        var sessionId = SessionId.New();
        var (viewerTransport, sharerTransport) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var viewer = new RemoteInputSession(sessionId, SessionRole.Viewer, viewerTransport);
        await using var sharer = new RemoteInputSession(sessionId, SessionRole.Sharer, sharerTransport, sink);
        var warnings = new List<string>();
        sharer.Warning += (_, warning) => warnings.Add(warning);

        await viewer.SetLocalCaptureEnabledAsync(true);
        var sends = new List<Task>();
        for (var index = 0; index < 2_500; index++)
        {
            var position = index % 101 / 100d;
            sends.Add(viewer.SendPointerMoveAsync(position, position));
        }
        await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(5));
        await viewer.SendKeyAsync(0x41, isPressed: true, isExtendedKey: false);

        Assert.DoesNotContain("input_rate_rejected", warnings);
        Assert.True(viewer.IsLocallyEnabled);
        Assert.InRange(sink.Inputs.Count(input => input.Kind == RemoteInputEventKind.PointerMove), 1, 8);
        Assert.Contains(sink.Inputs, input => input.Kind == RemoteInputEventKind.Key && input.VirtualKey == 0x41);
    }

    [Fact]
    public async Task Remote_input_rate_limit_disables_the_host_boundary()
    {
        var sessionId = SessionId.New();
        var (remote, local) = InMemoryTransport.CreatePair(SessionPermission.ControlInput);
        var sink = new FakeRemoteInputSink();
        sink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var receiver = new RemoteInputSession(sessionId, SessionRole.Sharer, local, sink);
        var warnings = new List<string>();
        receiver.Warning += (_, warning) => warnings.Add(warning);

        await remote.SendAsync(new RemoteInputFocusRequest
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionId = sessionId.Value,
            SessionGeneration = 1,
            FocusGeneration = 1,
            Sequence = 1,
            Enabled = true,
        });
        for (var sequence = 2; sequence <= 2_500; sequence++)
        {
            await remote.SendAsync(new RemoteInputCommand
            {
                InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
                SessionId = sessionId.Value,
                SessionGeneration = 1,
                FocusGeneration = 1,
                Sequence = sequence,
                Input = new RemoteInputEvent
                {
                    Kind = RemoteInputEventKind.PointerMove,
                    NormalizedX = 0.5,
                    NormalizedY = 0.5,
                },
            });
        }

        Assert.Equal(1_999, sink.Inputs.Count);
        Assert.Equal(1, sink.DisableCalls);
        Assert.Equal(["input_rate_rejected"], warnings);
    }

    private static TransferOffer CreateSingleFileOffer(Guid transferId, string name, byte[] payload) => new()
    {
        TransferId = transferId,
        DisplayName = name,
        TotalBytes = payload.Length,
        Entries =
        [
            new TransferEntry
            {
                RelativePath = name,
                Kind = TransferItemKind.File,
                Size = payload.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
            },
        ],
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class InMemoryTransport(SessionPermission permissions) : ICollaborationTransport
    {
        private InMemoryTransport? _peer;
        public SessionPermission Permissions { get; } = permissions;
        public bool IsReady => true;
        public bool IsInputAcknowledgementNegotiated { get; set; }
        public PeerClockEstimate? PeerClockEstimate { get; set; }
        public TimeSpan SendDelay { get; set; }
        public TaskCompletionSource? SendStarted { get; set; }
        public Task? SendBlocker { get; set; }
        public Func<CollaborationMessage, Exception?>? SendFailure { get; set; }
        public List<CollaborationMessage> SentMessages { get; } = [];
        public List<InputLatencyMeasurement> InputLatencyMeasurements { get; } = [];
        public List<(Guid TransferId, int DeliveredBytes, bool Flush)> DeliveryReports { get; } = [];
        public event EventHandler? Ready;
        public event EventHandler<CollaborationMessage>? MessageReceived;
        public event EventHandler<string>? ProtocolError;

        public static (InMemoryTransport Left, InMemoryTransport Right) CreatePair(SessionPermission permissions)
        {
            var left = new InMemoryTransport(permissions);
            var right = new InMemoryTransport(permissions);
            left._peer = right;
            right._peer = left;
            return (left, right);
        }

        public async Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendStarted?.TrySetResult();
            if (SendBlocker is not null) await SendBlocker.WaitAsync(cancellationToken);
            if (SendDelay > TimeSpan.Zero) await Task.Delay(SendDelay, cancellationToken);
            if (SendFailure?.Invoke(message) is { } failure) throw failure;
            SentMessages.Add(message);
            _peer!.MessageReceived?.Invoke(_peer, message);
        }

        public void ReportInputLatency(InputLatencyMeasurement measurement) =>
            InputLatencyMeasurements.Add(measurement);

        public ValueTask ReportFileDeliveryAsync(
            Guid transferId,
            int deliveredBytes,
            bool flush = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeliveryReports.Add((transferId, deliveredBytes, flush));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void AnnounceReady() => Ready?.Invoke(this, EventArgs.Empty);
        public void Reject(string reason) => ProtocolError?.Invoke(this, reason);
    }

    private sealed class FakeClipboard : IClipboardAdapter
    {
        public string? Text { get; private set; }
        public int RemoteWrites { get; private set; }
        public event EventHandler? ContentChanged;
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Text);
        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            RemoteWrites++;
            ContentChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken = default) { Text = null; return Task.CompletedTask; }
        public void SetLocal(string text) { Text = text; ContentChanged?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class FakeRemoteInputSink : IRemoteInputSink
    {
        private bool _enabled;
        public List<RemoteInputEvent> Inputs { get; } = [];
        public int DisableCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public void SetCaptureTarget(CaptureTargetInfo? target) { }
        public void RestoreApprovedScope(SessionPermission approvedPermissions) =>
            _enabled = approvedPermissions.HasFlag(SessionPermission.ControlInput);
        public void DisableAndReleaseAll()
        {
            DisableCalls++;
            _enabled = false;
        }
        public void ReleaseAll() => ReleaseCalls++;
        public bool TryInject(RemoteInputEvent input)
        {
            if (!_enabled) return false;
            Inputs.Add(input);
            return true;
        }
    }

    private sealed class MemoryProfileStore : ICollaborationProfileStore
    {
        public CollaborationProfileSnapshot Snapshot { get; private set; } = new();
        public Task<CollaborationProfileSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task SaveAsync(CollaborationProfileSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryAudit : ISecurityAuditLog
    {
        public List<SecurityAuditEvent> Events { get; } = [];
        public Task AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SecurityAuditEvent>> ReadRecentAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SecurityAuditEvent>>(Events.TakeLast(limit).ToArray());
        public Task<AuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditIntegrityResult(true, Events.Count, null));
        public Task ExportSanitizedJsonLinesAsync(string destinationFile, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ApplyRetentionAsync(TimeSpan retention, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ClearAsync(bool confirmed, CancellationToken cancellationToken = default)
        {
            if (!confirmed) throw new InvalidOperationException("Confirmation is required.");
            Events.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedPresenceProvider(DevicePresence presence) : IDevicePresenceProvider
    {
        public Task<IReadOnlyDictionary<PeerOnQId, DevicePresence>> QueryAsync(
            IReadOnlyList<PeerOnQId> deviceIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<PeerOnQId, DevicePresence>>(deviceIds.ToDictionary(id => id, _ => presence));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "peeronq-phase4-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
