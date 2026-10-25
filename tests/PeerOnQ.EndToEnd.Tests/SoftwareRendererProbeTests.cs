using Microsoft.Extensions.Logging;
using PeerOnQ.Platform.Windows.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

// The probe keeps process-wide state; its tests and the load-trace tests must not interleave.
[Collection(CollectionName)]
public sealed class SoftwareRendererProbeTests
{
    internal const string CollectionName = "Software renderer probe";

    [Fact]
    public void A_warp_device_is_reported_once_with_the_component_that_preceded_it()
    {
        // 0.9.85 field test: d3d10warp.dll was resident on one PC and no log said why.
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Warp,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out var device).CheckError();
        using (device)
        {
            Assert.True(SoftwareRendererProbe.IsWarpLoaded);
            SoftwareRendererProbe.ResetForTesting();
            var log = new CapturingLogger();

            SoftwareRendererProbe.ReportIfLoaded(log, "the test WARP device was created");
            SoftwareRendererProbe.ReportIfLoaded(log, "a later component");

            var message = Assert.Single(log.Messages);
            Assert.Contains("d3d10warp.dll", message, StringComparison.Ordinal);
            Assert.Contains("the test WARP device was created", message, StringComparison.Ordinal);
            Assert.Contains("Default graphics adapter", message, StringComparison.Ordinal);
            // 0.9.86: the property was named Component and overwrote the enricher's Component="desktop".
            Assert.DoesNotContain("Component", log.PropertyNames);
            Assert.Contains("Trigger", log.PropertyNames);
        }
    }

    [Fact]
    public void The_startup_step_after_which_warp_appeared_is_reported()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Warp,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out var device).CheckError();
        using (device)
        {
            // WARP cannot be unloaded again; it became resident during the step after this one.
            SoftwareRendererProbe.ResetForTesting(lastStep: "the step before the WARP device");
            SoftwareRendererProbe.Mark("the step that created the WARP device");
            SoftwareRendererProbe.Mark("a later step");
            var log = new CapturingLogger();

            SoftwareRendererProbe.ReportIfLoaded(log, "a later report");

            var message = Assert.Single(log.Messages);
            Assert.Contains("after the startup step the step before the WARP device", message, StringComparison.Ordinal);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<string> _messages = [];
        private readonly List<string> _propertyNames = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_gate) return [.. _messages]; }
        }

        public IReadOnlyList<string> PropertyNames
        {
            get { lock (_gate) return [.. _propertyNames]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
                if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                    _propertyNames.AddRange(properties.Select(property => property.Key));
            }
        }
    }
}
