using PeerOnQ.Platform.Windows;
using PeerOnQ.Platform.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>
/// The 60 fps minimum needs every encoder instance of a session, not only the first, to return a
/// frame within one 60 fps interval. Power-throttled, the AMD MFT returned frames after about 31 ms
/// from the second instance on (32 fps); the app opts out at startup (ProcessPowerThrottling).
/// </summary>
[Collection("Hardware video acceptance")]
public sealed class HardwareVideoThroughputTests(ITestOutputHelper output)
{
    private const double SixtyFpsBudgetMs = 1000.0 / 60;

    [HardwareVideoFact]
    public void Power_throttling_slows_the_hardware_encoder_and_the_opt_out_restores_it()
    {
        // Windows throttles a background process at its own discretion; force it to show the effect.
        Assert.True(SetThrottling(throttled: true));
        try
        {
            var throttled = Measure(1920, 1080, 10_000, out var name).Average();
            Assert.True(ProcessPowerThrottling.OptOut());
            var optedOut = Measure(1920, 1080, 10_000, out _).Average();
            output.WriteLine($"1080p mean encode: throttled={throttled:0.0} ms, opted out={optedOut:0.0} ms encoder={name}");
            Assert.True(optedOut < SixtyFpsBudgetMs * 0.75, $"opted out: {optedOut:0.0} ms");
            Assert.True(throttled > optedOut * 1.5, $"throttled {throttled:0.0} ms vs opted out {optedOut:0.0} ms");
        }
        finally { ProcessPowerThrottling.OptOut(); }
    }

    [HardwareVideoFact]
    public void Unthrottled_hardware_encoders_fit_the_60_fps_budget_on_every_instance()
    {
        Assert.True(ProcessPowerThrottling.OptOut(), "Windows refused the power-throttling opt-out.");
        // Three 1080p instances in a row, as rung changes create them, then one 1440p instance.
        foreach (var (width, height, kbps) in new[]
                 {
                     (1920, 1080, 10_000), (1920, 1080, 5_000), (1920, 1080, 2_000), (2560, 1440, 15_000),
                 })
        {
            var times = Measure(width, height, kbps, out var name);
            var mean = times.Average();
            var p95 = times[(int)(times.Count * 0.95)];
            output.WriteLine($"{width}x{height} {kbps} kbps: encode mean={mean:0.0} p95={p95:0.0} max={times[^1]:0.0} ms encoder={name}");
            Assert.True(mean < SixtyFpsBudgetMs * 0.75, $"{width}x{height}: mean encode {mean:0.0} ms");
            Assert.True(p95 < SixtyFpsBudgetMs, $"{width}x{height}: p95 encode {p95:0.0} ms");
        }
    }

    /// <summary>EcoQoS on (execution speed and timer resolution throttled) for this test process.</summary>
    private static bool SetThrottling(bool throttled)
    {
        const uint mask = 0x1 | 0x4;
        var state = new PowerThrottlingState { Version = 1, ControlMask = mask, StateMask = throttled ? mask : 0 };
        return SetProcessInformation(GetCurrentProcess(), 4, ref state,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<PowerThrottlingState>());
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref PowerThrottlingState information, uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    private static List<double> Measure(int width, int height, int kbps, out string name)
    {
        var page = TextPage(width, height * 2);
        using var encoder = new MediaFoundationH264Encoder();
        var i420 = new byte[width * height * 3 / 2];
        Array.Fill(i420, (byte)128, width * height, width * height / 2);
        var times = new List<double>();
        for (var index = 0; index < 150; index++)
        {
            Buffer.BlockCopy(page, index * 9 % height * width, i420, 0, width * height);
            var nv12 = MediaFoundationVideoUtilities.I420ToNv12(width, height, i420);
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.NotEmpty(encoder.EncodeNv12(width, height, nv12, kbps, 60, forceKeyFrame: false));
            // The first frames include MFT creation and the initial key frame.
            if (index >= 10) times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds);
        }
        times.Sort();
        name = encoder.Name;
        return times;
    }

    private static byte[] TextPage(int width, int height)
    {
        var page = new byte[width * height];
        Array.Fill(page, (byte)235);
        var random = new Random(42);
        for (var line = 8; line + 12 < height; line += 20)
            for (var x = 24 + random.Next(0, 40); x + 7 < width - 24; x += 8)
                for (var y = 0; y < 12; y++)
                {
                    var bits = random.Next(0, 128);
                    for (var column = 0; column < 7; column++)
                        if ((bits >> column & 1) != 0) page[(line + y) * width + x + column] = 40;
                }
        return page;
    }
}
