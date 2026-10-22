using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Platform.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class SystemAudioFactAttribute : FactAttribute
{
    public SystemAudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PEERONQ_SYSTEM_AUDIO_TEST") != "1")
        {
            Skip = "Set PEERONQ_SYSTEM_AUDIO_TEST=1 for the Windows default-render endpoint lifecycle smoke.";
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            Skip = "WASAPI system sound requires Windows.";
            return;
        }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }
        catch (COMException)
        {
            Skip = "No accessible default Windows render endpoint; physical system-audio acceptance was not performed.";
        }
    }
}

[CollectionDefinition("WASAPI system audio acceptance", DisableParallelization = true)]
public sealed class SystemAudioAcceptanceCollection;

[Collection("WASAPI system audio acceptance")]
public sealed class SystemAudioIntegrationTests(ITestOutputHelper output)
{
    [SystemAudioFact]
    public async Task Actual_default_endpoint_loopback_starts_stops_and_releases_without_audible_test_output()
    {
        for (var iteration = 0; iteration < 2; iteration++)
        {
            using var capture = new WindowsSystemAudioCapture();
            using var playback = new WindowsSystemAudioPlayback();
            var frames = 0;
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.FrameArrived += (_, frame) =>
            {
                try
                {
                    Assert.Equal(SystemAudioFrameCodec.PcmBytes, frame.Pcm.Length);
                    Assert.True(frame.CapturedAtUnixMicroseconds > 0);
                    Interlocked.Increment(ref frames);
                    received.TrySetResult();
                }
                finally { CryptographicOperations.ZeroMemory(frame.Pcm); }
            };
            capture.CaptureFailed += (_, _) => received.TrySetException(new IOException("WASAPI loopback stopped unexpectedly."));
            capture.Start();
            // An active silent render stream makes loopback callbacks possible even when the
            // computer is otherwise quiet. No tone or captured system content is played back.
            for (var index = 1; index <= 6; index++)
            {
                playback.Write(new SystemAudioFrame(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
                    index, new byte[SystemAudioFrameCodec.PcmBytes]));
                await Task.Delay(20);
            }
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            capture.Stop();
            var stoppedCount = Volatile.Read(ref frames);
            playback.Clear();
            await Task.Delay(100);
            Assert.Equal(stoppedCount, Volatile.Read(ref frames));
            output.WriteLine($"WASAPI lifecycle iteration={iteration + 1} capturedFrames={stoppedCount}; fixed48kHz stereo PCM16; silent output only.");
        }
    }
}
