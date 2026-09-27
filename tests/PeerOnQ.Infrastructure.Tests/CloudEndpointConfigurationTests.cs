using System.Reflection;
using System.Reflection.Emit;
using PeerOnQ.Infrastructure.Configuration;
using Xunit;

[assembly: AssemblyMetadata("PeerOnQApiBaseUrl", "https://api.example.com")]
[assembly: AssemblyMetadata("PeerOnQPresenceUrl", "https://presence.example.com")]
[assembly: AssemblyMetadata("PeerOnQSignalingUrl", "wss://signal.example.com/ws")]
[assembly: AssemblyMetadata("PeerOnQUpdatesBaseUrl", "https://updates.example.com")]
[assembly: AssemblyMetadata("PeerOnQDownloadsBaseUrl", "https://download.example.com")]
[assembly: AssemblyMetadata("PeerOnQDiagnosticsBaseUrl", "https://diagnostics.example.com")]
[assembly: AssemblyMetadata("PeerOnQDeploymentEnvironment", "Production")]
[assembly: AssemblyMetadata("PeerOnQRegion", "eu-west")]

namespace PeerOnQ.Infrastructure.Tests;

public sealed class CloudEndpointConfigurationTests
{
    [Fact]
    public void Complete_production_manifest_is_accepted()
    {
        var endpoints = CloudEndpointConfiguration.TryCreate(
            typeof(ProductionEndpointManifest).Assembly,
            allowDevelopmentEnvironmentOverrides: false);

        Assert.NotNull(endpoints);
        Assert.Equal(PeerOnQDeploymentEnvironment.Production, endpoints.Environment);
        Assert.Equal(Uri.UriSchemeHttps, endpoints.ApiBaseUri.Scheme);
        Assert.Equal("wss", endpoints.SignalingUri.Scheme);
        Assert.Equal("eu-west", endpoints.Region);
    }

    [Fact]
    public void Production_rejects_insecure_or_incomplete_manifest()
    {
        var insecure = CreateManifest(new Dictionary<string, string>
        {
            ["PeerOnQApiBaseUrl"] = "http://api.example.com",
            ["PeerOnQPresenceUrl"] = "https://presence.example.com",
            ["PeerOnQSignalingUrl"] = "wss://signal.example.com/ws",
            ["PeerOnQUpdatesBaseUrl"] = "https://updates.example.com",
            ["PeerOnQDownloadsBaseUrl"] = "https://download.example.com",
            ["PeerOnQDiagnosticsBaseUrl"] = "https://diagnostics.example.com",
            ["PeerOnQDeploymentEnvironment"] = "Production",
            ["PeerOnQRegion"] = "eu-west",
        });

        Assert.Throws<InvalidOperationException>(() => CloudEndpointConfiguration.TryCreate(
            insecure,
            allowDevelopmentEnvironmentOverrides: false));

        var incomplete = CreateManifest(new Dictionary<string, string>
        {
            ["PeerOnQApiBaseUrl"] = "https://api.example.com",
        });
        Assert.Throws<InvalidOperationException>(() => CloudEndpointConfiguration.TryCreate(
            incomplete,
            allowDevelopmentEnvironmentOverrides: false));
    }

    private static Assembly CreateManifest(IReadOnlyDictionary<string, string> values)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"PeerOnQ.EndpointTests.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        foreach (var (key, value) in values)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [key, value]));
        }

        return assembly;
    }
}

internal sealed class ProductionEndpointManifest;
