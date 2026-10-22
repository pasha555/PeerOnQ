using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.App.Linux.Tests;

[CollectionDefinition("Linux desktop", DisableParallelization = true)]
public sealed class LinuxDesktopCollection;

[Collection("Linux desktop")]
public sealed class LinuxWindowIntegrationTests
{
    [LinuxDesktopFact]
    public async Task ActualX11WindowRepaintsConsecutiveFramesAndClosesCleanly()
    {
        var previousUrl = Environment.GetEnvironmentVariable("PEERONQ_SIGNALING_URL");
        var previousData = Environment.GetEnvironmentVariable("PEERONQ_DATA_DIR");
        Environment.SetEnvironmentVariable("PEERONQ_SIGNALING_URL", "ws://127.0.0.1:9/ws");
        Environment.SetEnvironmentVariable("PEERONQ_DATA_DIR", Path.Combine(Path.GetTempPath(),
            "peeronq-linux-ui-" + Guid.NewGuid().ToString("N")));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
                Dispatcher.UIThread.Post(async () =>
                {
                    nint display = 0;
                    try
                    {
                        await using var services = await LinuxAppServices.CreateAsync(lifetime.Token);
                        var window = new MainWindow();
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        window.Closed += (_, _) => closed.TrySetResult();
                        window.Show();
                        await window.AttachAsync(services, lifetime.Token);
                        var session = SessionId.New();
                        typeof(MainWindow).GetField("_currentSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .SetValue(window, session);
                        var receive = typeof(MainWindow).GetMethod("OnRemoteFrameReceived",
                            BindingFlags.Instance | BindingFlags.NonPublic)!;
                        display = XOpenDisplay(0);
                        Assert.NotEqual(0, display);
                        var viewport = window.FindControl<Border>("RemoteViewport")!;
                        foreach (var (blue, red, expected) in new[] { (0, 255, 0xff0000u), (255, 0, 0x0000ffu) })
                        {
                            var pixels = new byte[8 * 8 * 3];
                            for (var pixel = 0; pixel < 64; pixel++)
                            {
                                pixels[pixel * 3] = (byte)blue;
                                pixels[pixel * 3 + 2] = (byte)red;
                            }
                            receive.Invoke(window, [null, new SessionRemoteFrame(session,
                                new RemoteVideoFrame(8, 8, pixels, RemotePixelFormat.Bgr24, TimeSpan.Zero))]);
                            var matched = false;
                            for (var attempt = 0; attempt < 100 && !matched; attempt++)
                            {
                                await Task.Delay(20, lifetime.Token);
                                var point = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2,
                                    viewport.Bounds.Height / 2), window);
                                if (point is null) continue;
                                var image = XGetImage(display, window.TryGetPlatformHandle()!.Handle,
                                    (int)(point.Value.X * window.RenderScaling),
                                    (int)(point.Value.Y * window.RenderScaling), 1, 1, nuint.MaxValue, 2);
                                if (image == 0) continue;
                                try { matched = ((uint)XGetPixel(image, 0, 0) & 0xffffff) == expected; }
                                finally { _ = XDestroyImage(image); }
                            }
                            Assert.True(matched, "The actual X11 viewport did not repaint its latest synthetic color.");
                        }
                        window.Close();
                        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
                        completion.TrySetResult();
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                    finally
                    {
                        if (display != 0) _ = XCloseDisplay(display);
                        await lifetime.CancelAsync();
                    }
                });
                Dispatcher.UIThread.MainLoop(lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "PeerOnQ Linux X11 acceptance" };
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
        finally
        {
            await lifetime.CancelAsync();
            var stopped = thread.Join(TimeSpan.FromSeconds(5));
            Environment.SetEnvironmentVariable("PEERONQ_SIGNALING_URL", previousUrl);
            Environment.SetEnvironmentVariable("PEERONQ_DATA_DIR", previousData);
            Assert.True(stopped, "The Linux UI thread did not stop.");
        }
    }

    private sealed class LinuxDesktopFactAttribute : FactAttribute
    {
        public LinuxDesktopFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("PEERONQ_LINUX_NATIVE_TEST") != "1"
                || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                Skip = "Requires actual X11 and an unlocked Secret Service; enable PEERONQ_LINUX_NATIVE_TEST=1.";
        }
    }

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern nint XGetImage(nint display, nint drawable,
        int x, int y, uint width, uint height, nuint mask, int format);
    [DllImport("libX11.so.6")] private static extern nuint XGetPixel(nint image, int x, int y);
    [DllImport("libX11.so.6")] private static extern int XDestroyImage(nint image);
}
