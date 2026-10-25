using System.Collections.Concurrent;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class BulkReceiveBackpressureTests
{
    [Fact]
    public async Task Bulk_burst_waits_for_slow_consumer_and_preserves_every_record_in_order()
    {
        using var releaseConsumer = new ManualResetEventSlim();
        await using var session = WebRtcMediaSession.CreateViewer(
            SessionId.New(), permissions: SessionPermission.ViewScreen | SessionPermission.FileTransfer);
        var firstRecord = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<byte>();
        var errors = new ConcurrentQueue<string>();
        session.SecurityError += (_, error) => errors.Enqueue(error);
        session.DataMessageReceived += (_, payload) =>
        {
            received.Enqueue(payload.Span[0]);
            if (payload.Span[0] == 0)
            {
                firstRecord.TrySetResult();
                releaseConsumer.Wait();
            }
            if (payload.Span[0] == 31) allDelivered.TrySetResult();
        };

        var producer = Task.Run(() =>
        {
            for (byte index = 0; index < 32; index++)
            {
                if (index == 9) queueFull.TrySetResult();
                session.QueueBulkRecord(new byte[] { index });
            }
        });
        try
        {
            await firstRecord.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queueFull.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(
                () => producer.WaitAsync(TimeSpan.FromMilliseconds(100)));
            releaseConsumer.Set();

            await producer.WaitAsync(TimeSpan.FromSeconds(5));
            await allDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Enumerable.Range(0, 32).Select(index => (byte)index), received);
            Assert.Empty(errors);
        }
        finally
        {
            releaseConsumer.Set();
            await producer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Closing_session_releases_full_bulk_queue_writer_without_waiting_for_consumer()
    {
        using var releaseConsumer = new ManualResetEventSlim();
        await using var session = WebRtcMediaSession.CreateViewer(
            SessionId.New(), permissions: SessionPermission.ViewScreen | SessionPermission.FileTransfer);
        var firstRecord = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<string>();
        session.SecurityError += (_, error) => errors.Enqueue(error);
        session.DataMessageReceived += (_, _) =>
        {
            firstRecord.TrySetResult();
            releaseConsumer.Wait();
        };

        var producer = Task.Run(() =>
        {
            for (byte index = 0; index < 10; index++)
            {
                if (index == 9) queueFull.TrySetResult();
                session.QueueBulkRecord(new byte[] { index });
            }
        });
        try
        {
            // Setup waits are generous for loaded CI runners. The close below must still finish
            // while the consumer stays blocked forever, so its bound remains meaningful.
            await firstRecord.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await queueFull.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAsync<TimeoutException>(
                () => producer.WaitAsync(TimeSpan.FromMilliseconds(100)));

            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await producer.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(releaseConsumer.IsSet);
            Assert.Empty(errors);
        }
        finally
        {
            releaseConsumer.Set();
            await producer.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
