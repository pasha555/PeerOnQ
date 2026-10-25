using System.Security.Cryptography;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed partial class Phase4CollaborationTests
{
    [Theory]
    [InlineData(false, 240 * 1024)]
    [InlineData(true, 256 * 1024)]
    public async Task File_chunk_limit_requires_the_negotiated_bulk_lane(bool bulkNegotiated, int expectedLimit)
    {
        var media = new FakeMediaSession(SessionId.New(), SessionRole.Sharer)
        {
            IsBulkDataLaneNegotiated = bulkNegotiated,
            IsNativeBulkTransportNegotiated = true,
        };
        await using var transport = new MediaCollaborationTransport(
            media, SessionPermission.FileTransfer, new RejectingHybridIdentityProvider(), new string('f', 64));
        Assert.Equal(expectedLimit, transport.MaximumFileChunkBytes);
    }

    [Fact]
    public void Legacy_full_chunk_with_worst_valid_path_and_metadata_fits_the_sctp_message_limit()
    {
        var path = SafeTransferPath.NormalizeRelative(new string('\u754c', 109) + "/" + new string('\u754c', 110));
        Assert.Equal(220, path.Length);
        var sessionId = SessionId.New();
        var transferId = Guid.NewGuid();
        var encoded = CollaborationProtocolCodec.Encode(new TransferChunk
        {
            SessionId = sessionId.Value,
            PermissionGeneration = long.MaxValue,
            TransferId = transferId,
            RelativePath = path,
            Offset = long.MaxValue,
            Index = long.MaxValue,
            ChunkSha256 = new string('f', 64),
            Payload = new byte[240 * 1024],
        });
        var secret = RandomNumberGenerator.GetBytes(64);
        using var protector = new SessionTrafficProtector(secret, sessionId, SessionRole.Sharer);
        CryptographicOperations.ZeroMemory(secret);
        var protectedRecord = protector.Protect(SecureChannelKind.FileTransfer, transferId, encoded);
        Assert.Equal(encoded.Length + SessionTrafficProtector.RecordOverheadBytes, protectedRecord.Length);
        Assert.InRange(protectedRecord.Length, 240 * 1024 + 1, 256 * 1024);
    }

    [Theory]
    [InlineData(256 * 1024, 240 * 1024, 240 * 1024)]
    [InlineData(128 * 1024, 240 * 1024, 128 * 1024)]
    [InlineData(256 * 1024, 256 * 1024, 256 * 1024)]
    public async Task Outbound_chunk_stride_is_capped_once_and_preserved_across_custom_options_and_resume(
        int configuredChunkBytes, int transportChunkBytes, int expectedChunkBytes)
    {
        using var sourceRoot = new TemporaryDirectory();
        using var destinationRoot = new TemporaryDirectory();
        var source = Path.Combine(sourceRoot.Path, "stride.bin");
        var expected = RandomNumberGenerator.GetBytes(1024 * 1024 + 13);
        await File.WriteAllBytesAsync(source, expected);
        var (senderTransport, receiverTransport) = InMemoryTransport.CreatePair(SessionPermission.FileTransfer);
        senderTransport.MaximumFileChunkBytes = transportChunkBytes;
        await using var sender = new FileTransferService(
            senderTransport, new FileTransferOptions { ChunkBytes = configuredChunkBytes });
        await using var receiver = new FileTransferService(receiverTransport);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptionRaised = 0;
        long? resumedOffset = null;
        receiverTransport.SendFailure = message =>
        {
            if (message is TransferResume resume) resumedOffset = resume.ReceivedOffsets["stride.bin"];
            return null;
        };
        receiver.TransferChanged += (_, transfer) =>
        {
            if (transfer.Status == TransferStatus.Transferring && transfer.TransferredBytes > 0
                && Interlocked.Exchange(ref interruptionRaised, 1) == 0)
            {
                sender.ConnectionInterrupted();
                receiver.ConnectionInterrupted();
                interrupted.TrySetResult();
            }
            if (transfer.Status == TransferStatus.Completed) completed.TrySetResult();
            if (transfer.Status == TransferStatus.Failed)
                completed.TrySetException(new InvalidOperationException(transfer.FailureCode));
        };
        sender.TransferChanged += (_, transfer) =>
        {
            if (transfer.Status == TransferStatus.Failed)
                completed.TrySetException(new InvalidOperationException(transfer.FailureCode));
        };

        var transferId = await sender.OfferAsync([source]);
        senderTransport.MaximumFileChunkBytes = 64 * 1024;
        await receiver.AcceptAsync(transferId, destinationRoot.Path, TransferCollisionPolicy.Rename);
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        receiver.ConnectionResumed();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var chunks = senderTransport.SentMessages.OfType<TransferChunk>().ToArray();
        Assert.Equal((expected.Length + expectedChunkBytes - 1) / expectedChunkBytes, chunks.Length);
        Assert.Equal(expectedChunkBytes, chunks[0].Payload.Length);
        for (var index = 0; index < chunks.Length; index++)
        {
            Assert.Equal(index, chunks[index].Index);
            Assert.Equal((long)index * expectedChunkBytes, chunks[index].Offset);
            Assert.InRange(chunks[index].Payload.Length, 1, expectedChunkBytes);
        }
        Assert.Single(receiverTransport.SentMessages.OfType<TransferResume>());
        Assert.Equal(expectedChunkBytes, resumedOffset);
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(destinationRoot.Path, "stride.bin")));
    }
}
