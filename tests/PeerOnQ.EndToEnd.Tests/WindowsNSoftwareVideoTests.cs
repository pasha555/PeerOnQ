using System.Runtime.InteropServices;
using PeerOnQ.Platform.Windows.Media;
using PeerOnQ.Platform.Windows.Rendering;
using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>
/// Windows N without the Media Feature Pack (LNK-363) must work by itself: no Media Foundation
/// factory is offered, nothing MF-backed is attempted, and the dashboard informs instead of asking
/// for an install.
/// </summary>
[Collection(SoftwareRendererProbeTests.CollectionName)]
public sealed class WindowsNSoftwareVideoTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Missing_media_foundation_is_reported_before_the_decoder_check()
    {
        var checkedFiles = new List<string>();
        var status = MediaFoundationAvailability.Evaluate(_ => false, path => { checkedFiles.Add(path); return true; });

        Assert.False(status.PlatformPresent);
        Assert.False(status.H264DecoderPresent);
        Assert.Contains("mfplat.dll", status.MissingReason, StringComparison.Ordinal);
        Assert.Empty(checkedFiles);
    }

    [Fact]
    public void A_missing_h264_decoder_keeps_the_platform_but_reports_the_decoder()
    {
        var status = MediaFoundationAvailability.Evaluate(_ => true, _ => false);

        Assert.True(status.PlatformPresent);
        Assert.False(status.H264DecoderPresent);
        Assert.Contains("msmpeg2vdec.dll", status.MissingReason, StringComparison.Ordinal);
        Assert.Null(MediaFoundationAvailability.Evaluate(_ => true, _ => true).MissingReason);
    }

    [Fact]
    public void Windows_n_gets_no_media_foundation_codec_factory()
    {
        // A null factory means the sharer never offers hardware H.264 and the viewer never offers
        // H.264 decoding: both negotiate VP8 without an MF call per session.
        Assert.Null(MediaFoundationCodecFactories.HardwareH264Encoder(platformPresent: false));
        Assert.Null(MediaFoundationCodecFactories.H264Decoder(decoderPresent: false, created: null));
        Assert.NotNull(MediaFoundationCodecFactories.HardwareH264Encoder(platformPresent: true));
        Assert.NotNull(MediaFoundationCodecFactories.H264Decoder(decoderPresent: true, created: null));
    }

    [Fact]
    public void The_hardware_encoder_probe_never_calls_media_foundation_when_it_is_missing()
    {
        var probed = false;
        var encoders = HardwareEncoderProbe.Enumerate(platformPresent: false, () =>
        {
            probed = true;
            return [new HardwareEncoderInfo(HardwareVideoCodec.H264, "unexpected")];
        });

        Assert.Empty(encoders);
        Assert.False(probed);
    }

    [Fact]
    public void A_failing_hardware_encoder_probe_means_software_only_instead_of_an_exception()
    {
        var encoders = HardwareEncoderProbe.Enumerate(platformPresent: true,
            () => throw new EntryPointNotFoundException("MFTEnumEx"));

        Assert.Empty(encoders);
    }

    [Fact]
    public void The_dashboard_notice_informs_instead_of_asking_for_the_media_feature_pack()
    {
        var notice = MediaFoundationAvailability.SoftwareVideoNotice;

        Assert.Contains("software video", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("Install", notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Media Feature Pack", notice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("d3d10warp.dll", true)]
    [InlineData("D3D10WARP.DLL", true)]
    [InlineData("d3d10warp.dll.mui", false)]
    [InlineData("d3d11.dll", false)]
    [InlineData("", false)]
    public void The_loader_notification_matches_only_the_traced_module(string loaded, bool expected) =>
        Assert.Equal(expected, ModuleLoadTracer.Matches(loaded, SoftwareRendererProbe.WarpModule));

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0xE8, 1, 2, 3, 4 }, true)] // call rel32
    [InlineData(new byte[] { 0, 0, 0xFF, 0x15, 1, 2, 3, 4 }, true)] // call [rip+disp32]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0xFF, 0x53, 8 }, true)] // call [rbx+8]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0xFF, 0xD0 }, true)] // call rax
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0xFF, 0xE0 }, false)] // jmp rax
    [InlineData(new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, false)]
    public void Return_addresses_must_follow_an_x64_call(byte[] before, bool expected) =>
        Assert.Equal(expected, ModuleLoadTracer.IsX64Call(before));

    [Theory]
    [InlineData(0x94000010u, true)] // bl
    [InlineData(0xD63F0100u, true)] // blr x8
    [InlineData(0xD63F0A1Fu, true)] // blraaz x16
    [InlineData(0xD61F0100u, false)] // br x8
    [InlineData(0xD503201Fu, false)] // nop
    public void Return_addresses_must_follow_an_arm64_call(uint instruction, bool expected) =>
        Assert.Equal(expected, ModuleLoadTracer.IsArm64Call(instruction));

    [Fact]
    public void A_traced_module_load_names_the_loading_thread_and_its_callers()
    {
        var module = FindUnloadedSystemModule();
        if (module is null) return; // Every candidate is already resident in this test process.
        ModuleLoadTrace? captured = null;
        using var tracer = ModuleLoadTracer.Start(module, trace => Volatile.Write(ref captured, trace));
        Assert.True(tracer.IsRegistered);

        var handle = LoadOnThisThread(module);
        Assert.NotEqual(nint.Zero, handle);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref captured) is null && DateTime.UtcNow < deadline) Thread.Sleep(20);

        var trace = Assert.IsType<ModuleLoadTrace>(Volatile.Read(ref captured));
        output.WriteLine($"native callers: {trace.NativeCallers}");
        Assert.Same(trace, tracer.Trace);
        Assert.Equal(module, trace.Module);
        Assert.Equal(Environment.CurrentManagedThreadId, trace.ManagedThreadId);
        Assert.Contains(nameof(LoadOnThisThread), trace.ManagedStack, StringComparison.Ordinal);
        Assert.Contains("ntdll.dll", trace.NativeCallers, StringComparison.OrdinalIgnoreCase);
        // One capture per tracer: the registration ends after the match.
        var unregisterDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (tracer.IsRegistered && DateTime.UtcNow < unregisterDeadline) Thread.Sleep(20);
        Assert.False(tracer.IsRegistered);
    }

    [Fact]
    public void A_warp_device_creation_is_traced_to_direct3d_and_reported_once()
    {
        ModuleLoadTrace? captured = null;
        using var tracer = ModuleLoadTracer.Start(SoftwareRendererProbe.WarpModule, trace => Volatile.Write(ref captured, trace));
        // WARP cannot be unloaded once another test created it. On a machine without a GPU
        // (CI runners) the Basic Render Driver is d3d10warp.dll too, so hardware-device tests in
        // other collections load it concurrently; checking after Start closes that window.
        if (SoftwareRendererProbe.IsWarpLoaded) return;
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(
            null,
            Vortice.Direct3D.DriverType.Warp,
            Vortice.Direct3D11.DeviceCreationFlags.BgraSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1],
            out var device).CheckError();
        using (device)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Volatile.Read(ref captured) is null && DateTime.UtcNow < deadline) Thread.Sleep(20);
            var trace = Assert.IsType<ModuleLoadTrace>(Volatile.Read(ref captured));
            output.WriteLine($"native callers: {trace.NativeCallers}");
            if (trace.ManagedThreadId != Environment.CurrentManagedThreadId
                && !trace.ManagedStack.Contains(nameof(A_warp_device_creation_is_traced_to_direct3d_and_reported_once), StringComparison.Ordinal))
                return; // Another test's device loaded WARP between Start and this creation.
            Assert.Contains("d3d11.dll", trace.NativeCallers, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(nameof(A_warp_device_creation_is_traced_to_direct3d_and_reported_once), trace.ManagedStack,
                StringComparison.Ordinal);

            SoftwareRendererProbe.ResetForTesting();
            var log = new MessageLogger();
            SoftwareRendererProbe.ReportLoadTrace(log, trace);
            SoftwareRendererProbe.ReportLoadTrace(log, trace);
            var message = Assert.Single(log.Messages);
            Assert.Contains("loaded by thread", message, StringComparison.Ordinal);
            Assert.Contains("d3d11.dll", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Warp_resident_before_tracing_is_reported_as_an_unknown_requester()
    {
        SoftwareRendererProbe.ResetForTesting();
        var log = new MessageLogger();

        SoftwareRendererProbe.ReportLoadTrace(log, trace: null);

        // Without a trace and without the "already resident" note nothing is claimed.
        Assert.Empty(log.Messages);
    }

    private sealed class MessageLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages { get { lock (_messages) return [.. _messages]; } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void Other_module_loads_are_ignored()
    {
        var module = FindUnloadedSystemModule(skip: 1);
        if (module is null) return;
        ModuleLoadTrace? captured = null;
        using var tracer = ModuleLoadTracer.Start("peeronq-never-loaded.dll", trace => captured = trace);

        LoadOnThisThread(module);
        Thread.Sleep(200);

        Assert.Null(captured);
        Assert.Null(tracer.Trace);
        Assert.True(tracer.IsRegistered);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static nint LoadOnThisThread(string module) =>
        NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, module));

    private static string? FindUnloadedSystemModule(int skip = 0) =>
        new[] { "msimg32.dll", "pdh.dll", "hid.dll", "mpr.dll", "wtsapi32.dll", "netapi32.dll", "dsrole.dll", "srvcli.dll" }
            .Where(name => File.Exists(Path.Combine(Environment.SystemDirectory, name)) && GetModuleHandle(name) == 0)
            .Skip(skip)
            .FirstOrDefault();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(string moduleName);
}
