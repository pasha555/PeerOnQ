using System.Text.Json;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.App;

/// <summary>
/// Local acceptance harness for the real viewer window: `PeerOnQ.exe --viewer-self-test
/// &lt;result.json&gt;`. It opens a ViewerWindow with synthetic frames, exercises every scale mode,
/// a frame-size change, the monitor arrows, separate monitor windows and the voice control, then
/// writes a result and exits.
/// It never contacts a server or a peer. A native XAML/render failure ends the process with the
/// failure code instead of a result file, which is exactly what this harness exists to catch.
/// </summary>
internal static class ViewerSelfTest
{
    public const string Argument = "--viewer-self-test";

    public static string? RequestedResultPath()
    {
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(arguments, value => value.Equals(Argument, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        return index + 1 < arguments.Length
            ? Path.GetFullPath(arguments[index + 1])
            : Path.Combine(Path.GetTempPath(), "peeronq-viewer-self-test.json");
    }

    public static async void Run(string resultPath)
    {
        var steps = new List<string>();
        var rendered = 0;
        var monitorWindows = 0;
        var monitorFrames = 0;
        var dialogsPassed = false;
        try
        {
            var info = new ActiveSessionInfo(
                SessionId.New(), SessionRole.Viewer, "Viewer self-test", "000-***-***-000",
                SessionMode.ViewOnly, SessionState.ConnectedViewOnly, DateTimeOffset.UtcNow);
            var viewer = new ViewerWindow(
                info,
                () => new MediaStatistics(),
                () => Task.CompletedTask,
                onRendered: _ => Interlocked.Increment(ref rendered),
                onSelectDisplay: _ => Task.CompletedTask,
                isVoiceAvailable: () => true,
                isVoiceEnabled: () => false,
                onSetVoice: _ => false);
            steps.Add("constructed");
            viewer.Activate();
            steps.Add("activated");
            viewer.SetRemoteDisplays(
                [
                    new CaptureTargetInfo(CaptureTargetKind.Display, "a", "Display 1", 1920, 1080),
                    new CaptureTargetInfo(CaptureTargetKind.Display, "b", "Display 2", 1280, 720),
                ],
                "a");
            steps.Add("displays");

            using var stop = new CancellationTokenSource();
            var small = 0;
            var pump = Task.Run(async () =>
            {
                var frame = 0;
                while (!stop.IsCancellationRequested)
                {
                    var (width, height) = Volatile.Read(ref small) switch
                    {
                        0 => (1920, 1080),
                        1 => (1280, 720),
                        _ => (3200, 1080),
                    };
                    var pixels = new byte[width * height * 4];
                    var shade = (byte)(frame++ * 7);
                    for (var index = 0; index < pixels.Length; index += 4)
                    {
                        pixels[index] = shade;
                        pixels[index + 1] = (byte)(index / 4 % width);
                        pixels[index + 2] = (byte)(index / 4 / width);
                        pixels[index + 3] = 255;
                    }
                    viewer.Present(new RemoteVideoFrame(width, height, pixels, RemotePixelFormat.Bgra32, TimeSpan.Zero)
                    {
                        BufferOwnershipCanTransfer = true,
                    });
                    await Task.Delay(33);
                }
            });

            await Task.Delay(1500);
            steps.Add($"frames:{Volatile.Read(ref rendered)}");
            foreach (var mode in new[] { 1, 2, 3, 0 })
            {
                viewer.ApplyScaleModeForSelfTest(mode);
                await Task.Delay(400);
                steps.Add($"mode:{mode}");
            }
            Volatile.Write(ref small, 1);
            await Task.Delay(800);
            steps.Add("resized");
            viewer.StepMonitorForSelfTest(1);
            viewer.StepMonitorForSelfTest(-1);
            steps.Add("monitors");
            await Task.Delay(400);
            var rendererBeforeSeparateWindows = viewer.RendererNameForSelfTest;

            // Separate windows: a combined 3200x1080 view of two placed displays opens one extra
            // window, and both windows draw their own crop of every frame.
            viewer.SetRemoteDisplays(
                [
                    new CaptureTargetInfo(CaptureTargetKind.Display, "a", "Display 1", 1920, 1080, 0, 0),
                    new CaptureTargetInfo(CaptureTargetKind.Display, "b", "Display 2", 1280, 720, 1920, 0),
                    new CaptureTargetInfo(CaptureTargetKind.Display, "all", "All displays", 3200, 1080, 0, 0),
                ],
                "all");
            Volatile.Write(ref small, 2);
            await Task.Delay(300);
            viewer.RequestSeparateWindowsForSelfTest();
            await Task.Delay(1500);
            monitorWindows = viewer.MonitorWindowCountForSelfTest;
            monitorFrames = viewer.MonitorWindowFramesForSelfTest;
            steps.Add($"separate:{monitorWindows}:{monitorFrames}");
            await Task.Delay(1200);

            await stop.CancelAsync();
            await pump;

            // The host-side indicator carries the voice toggle; load and expand it as well.
            var indicator = new SharingIndicatorWindow(
                info with { Role = SessionRole.Sharer },
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                isVoiceAvailable: () => true,
                isVoiceEnabled: () => false,
                onSetVoice: _ => false);
            indicator.Activate();
            indicator.ExpandForSelfTest();
            await Task.Delay(800);
            indicator.CloseAfterSessionEnded();
            steps.Add("indicator");

            // Permission dialogs (field crash in 0.9.84 and since 0.9.17): an unanswered request,
            // a second request while the first dialog is open, and a request the peer withdraws
            // must all end as timeouts, one dialog at a time, without ending the process.
            var host = new PermissionDialogHost(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), () => viewer.Content?.XamlRoot);
            PermissionRequest Request(int seconds) => new()
            {
                SessionId = SessionId.New(),
                RequesterId = PeerOnQ.Domain.Identity.PeerOnQId.NewId(),
                RequesterDisplayName = "Self-test requester",
                RequestedMode = SessionMode.ViewOnly,
                RequestedAt = DateTimeOffset.UtcNow,
                Timeout = TimeSpan.FromSeconds(seconds),
            };
            using var withdraw = new CancellationTokenSource();
            var unanswered = host.AskForScopeAsync(Request(2));
            var concurrent = host.AskForScopeAsync(Request(4));
            var withdrawn = host.AskForScopeAsync(Request(8), withdraw.Token);
            await Task.Delay(TimeSpan.FromSeconds(5));
            withdraw.Cancel();
            var answers = await Task.WhenAll(unanswered, concurrent, withdrawn).WaitAsync(TimeSpan.FromSeconds(15));
            dialogsPassed = answers.All(answer => answer.Decision == PermissionDecision.Timeout);
            steps.Add($"dialogs:{string.Join(',', answers.Select(answer => answer.Decision))}");

            var renderer = rendererBeforeSeparateWindows;
            // Separate windows work with either renderer: the swap chain hands over to the bitmap crops.
            var separateWindowsPassed = monitorWindows == 1 && monitorFrames > 5
                                        && viewer.RendererNameForSelfTest == "bitmap";
            var failure = viewer.RenderFailureForSelfTest;
            var passed = failure is null && Volatile.Read(ref rendered) > 10 && separateWindowsPassed && dialogsPassed;
            // Closing the last window ends a WinUI process, so the result is written first.
            steps.Add("closing");
            Write(resultPath, passed, renderer, Volatile.Read(ref rendered), monitorFrames, steps, failure);
            viewer.Close();
            Environment.Exit(passed ? 0 : 2);
        }
        catch (Exception ex)
        {
            Write(resultPath, false, null, Volatile.Read(ref rendered), monitorFrames, steps, ex.ToString());
            Environment.Exit(3);
        }
    }

    private static void Write(
        string path, bool passed, string? renderer, int rendered, int monitorFrames, List<string> steps, string? error)
    {
        var json = JsonSerializer.Serialize(new { passed, renderer, rendered, monitorFrames, steps, error });
        File.WriteAllText(path, json);
    }
}
