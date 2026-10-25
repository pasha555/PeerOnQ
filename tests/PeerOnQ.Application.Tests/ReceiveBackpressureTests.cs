using System.Collections.Concurrent;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class ReceiveBackpressureTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task File_receive_waits_for_its_consumer_or_cancellation_while_input_dispatch_stays_async(
        bool fileRecord, bool cancel)
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer receive bound");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer receive bound");
        using var releaseConsumer = new ManualResetEventSlim();
        var sessionId = SessionId.New();
        var permissions = SessionPermission.ViewScreen | SessionPermission.ControlInput | SessionPermission.FileTransfer;
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.DirectLan },
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.DirectLan },
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        await using var viewer = new MediaCollaborationTransport(
            viewerMedia, permissions, viewerIdentity.Service, sharerIdentity.LegacyFingerprint);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia, permissions, sharerIdentity.Service, viewerIdentity.LegacyFingerprint);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Ready += (_, _) => viewerReady.TrySetResult();
        sharer.Ready += (_, _) => sharerReady.TrySetResult();
        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        await Task.WhenAll(viewerReady.Task, sharerReady.Task).WaitAsync(TimeSpan.FromSeconds(10));

        // Record two real authenticated frames before deliberately contending the receiver gate.
        viewerMedia.DataPeer = null;
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            CollaborationMessage message = fileRecord
                ? new TransferPause { TransferId = Guid.NewGuid() }
                : new RemoteInputReleaseAll
                {
                    InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
                    SessionGeneration = 1,
                    FocusGeneration = 1,
                    Sequence = sequence,
                };
            await viewer.SendAsync(message);
        }
        var firstFrame = viewerMedia.SentData[^2];
        var secondFrame = viewerMedia.SentData[^1];
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<string>();
        var received = 0;
        sharer.ProtocolError += (_, error) => errors.Enqueue(error);
        sharer.MessageReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref received) == 1)
            {
                firstEntered.TrySetResult();
                releaseConsumer.Wait();
            }
            else secondHandled.TrySetResult();
        };

        var firstDispatch = Task.Run(() => sharerMedia.EmitData(firstFrame));
        Task secondDispatch = Task.CompletedTask;
        Task? disposal = null;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondDispatch = Task.Run(() =>
            {
                secondEntered.TrySetResult();
                sharerMedia.EmitData(secondFrame);
            });
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (fileRecord)
                await Assert.ThrowsAsync<TimeoutException>(
                    () => secondDispatch.WaitAsync(TimeSpan.FromMilliseconds(100)));
            else
                await secondDispatch.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref received));

            if (cancel)
            {
                disposal = sharer.DisposeAsync().AsTask();
                await secondDispatch.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(releaseConsumer.IsSet);
                Assert.False(disposal.IsCompleted);
            }
            releaseConsumer.Set();
            await Task.WhenAll(firstDispatch, secondDispatch).WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel)
            {
                await disposal!.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, received);
            }
            else
            {
                await secondHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, received);
            }
            Assert.Empty(errors);
        }
        finally
        {
            releaseConsumer.Set();
            await Task.WhenAll(firstDispatch, secondDispatch).WaitAsync(TimeSpan.FromSeconds(5));
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
