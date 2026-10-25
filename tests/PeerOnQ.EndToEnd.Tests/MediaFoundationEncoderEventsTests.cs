using PeerOnQ.Platform.Windows.Media;
using Vortice.MediaFoundation;
using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class MediaFoundationEncoderEventsTests
{
    [Fact]
    public void Early_events_keep_order_and_an_empty_zero_deadline_times_out()
    {
        var queue = new MediaFoundationEncoderEventQueue();
        Assert.True(queue.Publish(MediaEventTypes.TransformNeedInput));
        Assert.True(queue.Publish(MediaEventTypes.TransformHaveOutput));
        Assert.Equal(MediaEventTypes.TransformNeedInput, queue.Next(TimeSpan.Zero));
        Assert.Equal(MediaEventTypes.TransformHaveOutput, queue.Next(TimeSpan.Zero));
        Assert.Throws<TimeoutException>(() => queue.Next(TimeSpan.Zero));
    }

    [Fact]
    public void Queue_overflow_is_latched_instead_of_dropping_an_ordered_event()
    {
        var queue = new MediaFoundationEncoderEventQueue();
        for (var index = 0; index < MediaFoundationEncoderEventQueue.Capacity; index++)
            Assert.True(queue.Publish(MediaEventTypes.TransformNeedInput));
        Assert.False(queue.Publish(MediaEventTypes.TransformHaveOutput));
        Assert.False(queue.Publish(MediaEventTypes.TransformHaveOutput));
        Assert.Throws<InvalidOperationException>(() => queue.Next(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_waiter_wakes_on_error_or_stop_and_late_events_are_rejected(bool stop)
    {
        var queue = new MediaFoundationEncoderEventQueue();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = Task.Run(() =>
        {
            began.SetResult();
            return Record.Exception(() => queue.Next(TimeSpan.FromSeconds(10)));
        });
        await began.Task;
        if (stop) queue.Stop(); else queue.Fail(new InvalidOperationException("Injected native status error."));
        var error = await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        if (stop) Assert.IsType<ObjectDisposedException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.False(queue.Publish(MediaEventTypes.TransformNeedInput));
    }

    [Fact]
    public async Task Publication_racing_the_wait_does_not_lose_a_wakeup()
    {
        var queue = new MediaFoundationEncoderEventQueue();
        for (var index = 0; index < 64; index++)
        {
            var waiter = Task.Run(() => queue.Next(TimeSpan.FromSeconds(2)));
            Assert.True(queue.Publish(MediaEventTypes.TransformHaveOutput));
            Assert.Equal(MediaEventTypes.TransformHaveOutput, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task A_positive_deadline_expires_without_a_native_event()
    {
        var queue = new MediaFoundationEncoderEventQueue();
        var error = await Task.Run(() => Record.Exception(() => queue.Next(TimeSpan.FromMilliseconds(20))))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<TimeoutException>(error);
    }

    [Fact]
    public void Partial_subscribe_failure_keeps_the_callback_root_until_shutdown_then_releases_once()
    {
        var released = 0;
        var shutdown = false;
        IMFAsyncCallback? registered = null;
        var events = new MediaFoundationEncoderEvents(
            _ => MediaEventTypes.TransformNeedInput,
            callback => { registered = callback; throw new InvalidOperationException("Injected partial BeginGetEvent failure."); },
            () => { Assert.True(shutdown); released++; });
        Assert.Throws<InvalidOperationException>(events.Start);
        Assert.Same(events, registered);
        Assert.Equal(0, released);
        events.Stop();
        shutdown = true; // Owning encoder synchronously closes its native transform here.
        events.Dispose(); events.Dispose();
        Assert.Equal(1, released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Callback_read_or_resubscribe_failure_wakes_the_consumer_without_crossing_COM(bool failRead)
    {
        var subscriptions = 0;
        using var events = new MediaFoundationEncoderEvents(
            _ => failRead ? throw new InvalidOperationException("Injected EndGetEvent failure.") : MediaEventTypes.TransformNeedInput,
            _ => { if (++subscriptions == 2) throw new InvalidOperationException("Injected subsequent BeginGetEvent failure."); },
            () => { });
        events.Start();
        Assert.True(events.Invoke(null!).Success);
        Assert.Throws<InvalidOperationException>(() => events.Next(TimeSpan.Zero));
        Assert.Equal(failRead ? 1 : 2, subscriptions);
    }

    [Fact]
    public void Native_error_event_is_fatal_and_late_callbacks_do_not_touch_disposed_native_state()
    {
        var reads = 0; var subscriptions = 0; var releases = 0;
        var events = new MediaFoundationEncoderEvents(
            _ => { reads++; return MediaEventTypes.Error; },
            _ => subscriptions++, () => releases++);
        events.Start();
        Assert.True(events.Invoke(null!).Success);
        Assert.Throws<InvalidOperationException>(() => events.Next(TimeSpan.Zero));
        events.Stop(); events.Dispose(); events.Dispose();
        Assert.True(events.Invoke(null!).Success);
        Assert.Equal(1, reads); Assert.Equal(1, subscriptions); Assert.Equal(1, releases);
        Assert.Throws<ObjectDisposedException>(() => events.Next(TimeSpan.Zero));
    }

    [Fact]
    public async Task Stop_fences_an_active_callback_and_prevents_rearming_after_native_shutdown()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var subscriptions = 0;
        var releases = 0;
        var readFinished = false;
        using var events = new MediaFoundationEncoderEvents(
            _ => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(2))); readFinished = true; return MediaEventTypes.TransformNeedInput; },
            _ => subscriptions++, () => { Assert.True(readFinished); releases++; });
        events.Start();
        var callback = Task.Run(() => events.Invoke(null!));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        var stopBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = Task.Run(() => { stopBegan.SetResult(); events.Stop(); });
        try
        {
            await stopBegan.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromMilliseconds(50))));
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, releases);
        }
        finally { release.Set(); }
        await Task.WhenAll(callback, stop).WaitAsync(TimeSpan.FromSeconds(2));
        var before = subscriptions;
        Assert.True(events.Invoke(null!).Success);
        Assert.Equal(before, subscriptions);
        Assert.Throws<ObjectDisposedException>(() => events.Next(TimeSpan.Zero));
        events.Dispose();
        Assert.Equal(1, releases);
    }
}
