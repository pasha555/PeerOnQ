using System.Text.Json;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.App;

/// <summary>
/// Local acceptance harness for the real viewer window: `PeerOnQ.exe --viewer-self-test
/// &lt;result.json&gt;`. It opens a ViewerWindow with synthetic frames, exercises every scale mode,
/// a frame-size change, the monitor arrows and the voice control, then writes a result and exits.
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
                    var (width, height) = Volatile.Read(ref small) == 0 ? (1920, 1080) : (1280, 720);
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

            var renderer = viewer.RendererNameForSelfTest;
            var failure = viewer.RenderFailureForSelfTest;
            var passed = failure is null && Volatile.Read(ref rendered) > 10;
            // Closing the last window ends a WinUI process, so the result is written first.
            steps.Add("closing");
            Write(resultPath, passed, renderer, Volatile.Read(ref rendered), steps, failure);
            viewer.Close();
            Environment.Exit(passed ? 0 : 2);
        }
        catch (Exception ex)
        {
            Write(resultPath, false, null, Volatile.Read(ref rendered), steps, ex.ToString());
            Environment.Exit(3);
        }
    }

    private static void Write(string path, bool passed, string? renderer, int rendered, List<string> steps, string? error)
    {
        var json = JsonSerializer.Serialize(new { passed, renderer, rendered, steps, error });
        File.WriteAllText(path, json);
    }
}
