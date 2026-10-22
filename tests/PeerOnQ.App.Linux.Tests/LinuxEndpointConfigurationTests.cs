using System.Reflection;
using PeerOnQ.Infrastructure.Configuration;
using Xunit;

namespace PeerOnQ.App.Linux.Tests;

[Collection("Linux desktop")]
public sealed class LinuxEndpointConfigurationTests
{
    [Fact]
    public void EmptyDefaultBuildMetadataUsesOnlyExplicitlyAllowedCompleteEnvironmentEndpoints()
    {
        var assembly = typeof(LinuxAppServices).Assembly;
        var names = new Dictionary<string, (string Metadata, string Value)>
        {
            ["PEERONQ_API_BASE_URL"] = ("PeerOnQApiBaseUrl", "https://api.example.test/"),
            ["PEERONQ_PRESENCE_URL"] = ("PeerOnQPresenceUrl", "https://presence.example.test/"),
            ["PEERONQ_SIGNALING_URL"] = ("PeerOnQSignalingUrl", "wss://signal.example.test/ws"),
            ["PEERONQ_UPDATES_BASE_URL"] = ("PeerOnQUpdatesBaseUrl", "https://updates.example.test/"),
            ["PEERONQ_DOWNLOADS_BASE_URL"] = ("PeerOnQDownloadsBaseUrl", "https://downloads.example.test/"),
            ["PEERONQ_DIAGNOSTICS_BASE_URL"] = ("PeerOnQDiagnosticsBaseUrl", "https://diagnostics.example.test/"),
            ["PEERONQ_DEPLOYMENT_ENVIRONMENT"] = ("PeerOnQDeploymentEnvironment", "Production"),
            ["PEERONQ_REGION"] = ("PeerOnQRegion", "linux-preview"),
        };
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        foreach (var (_, item) in names)
            Assert.True(metadata.TryGetValue(item.Metadata, out var value) && string.IsNullOrWhiteSpace(value));
        var previous = names.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, item) in names) Environment.SetEnvironmentVariable(name, item.Value);
            Assert.Null(CloudEndpointConfiguration.TryCreate(assembly, allowDevelopmentEnvironmentOverrides: false));
            var endpoints = CloudEndpointConfiguration.TryCreate(assembly, allowDevelopmentEnvironmentOverrides: true);
            Assert.NotNull(endpoints);
            Assert.Equal("https://api.example.test/", endpoints.ApiBaseUri.AbsoluteUri);
            Assert.Equal("wss://signal.example.test/ws", endpoints.SignalingUri.AbsoluteUri);
            Assert.Equal(PeerOnQDeploymentEnvironment.Production, endpoints.Environment);
            Assert.Equal("linux-preview", endpoints.Region);

            Environment.SetEnvironmentVariable("PEERONQ_DIAGNOSTICS_BASE_URL", null);
            Assert.Throws<InvalidOperationException>(() => CloudEndpointConfiguration.TryCreate(assembly, true));
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
