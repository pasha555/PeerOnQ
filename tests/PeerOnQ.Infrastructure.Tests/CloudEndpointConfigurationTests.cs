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

    [Fact]
    public void Account_portal_defaults_to_official_host_without_enrollment_or_an_account()
    {
        var standalone = CreateManifest(new Dictionary<string, string>());

        Assert.Null(CloudEndpointConfiguration.TryCreate(standalone, false));
        var portal = CloudEndpointConfiguration.ResolveAccountPortalUri(standalone, false);
        Assert.Equal("https://portal.peeronq.com/", portal.AbsoluteUri);
        Assert.Empty(portal.UserInfo);
        Assert.Empty(portal.Query);
        Assert.Empty(portal.Fragment);
    }

    [Theory]
    [InlineData("https://portal.example.com", "Production", false)]
    [InlineData("https://portal.dev.localhost:5443", "Development", true)]
    [InlineData("http://localhost:23588", "Development", true)]
    [InlineData("http://127.0.0.1:23588", "Development", true)]
    [InlineData("http://[::1]:23588", "Development", true)]
    public void Account_portal_uses_compiled_deployment_metadata(string url, string environment, bool allowDevelopment)
    {
        var assembly = CreateManifest(new Dictionary<string, string>
        {
            ["PeerOnQAccountPortalUrl"] = url,
            ["PeerOnQDeploymentEnvironment"] = environment,
        });

        Assert.Equal(url + "/", CloudEndpointConfiguration.ResolveAccountPortalUri(assembly, allowDevelopment).AbsoluteUri);
        Assert.Null(CloudEndpointConfiguration.TryCreate(assembly, false));
    }

    [Theory]
    [InlineData("http://portal.peeronq.com", "Development", true)]
    [InlineData("http://localhost:23588", "Production", true)]
    [InlineData("http://localhost:23588", "Staging", true)]
    [InlineData("http://localhost:23588", "Development", false)]
    [InlineData("http://localhost:23588", "", true)]
    [InlineData("http://192.168.1.2:23588", "Development", true)]
    [InlineData("http://localhost.example.com", "Development", true)]
    [InlineData("https://user:password@portal.peeronq.com", "Production", false)]
    [InlineData("https://portal.peeronq.com/?device_token=test-value", "Production", false)]
    [InlineData("https://portal.peeronq.com/?installation_token=test-value", "Production", false)]
    [InlineData("https://portal.peeronq.com/#access_token=test-value", "Production", false)]
    [InlineData("https://portal.peeronq.com/?redirect=https://example.com", "Production", false)]
    [InlineData("file:///portal.html", "Development", true)]
    [InlineData("javascript:alert(1)", "Production", false)]
    [InlineData("/portal", "Production", false)]
    public void Account_portal_rejects_unsafe_metadata(string url, string environment, bool allowDevelopment)
    {
        var assembly = CreateManifest(new Dictionary<string, string>
        {
            ["PeerOnQAccountPortalUrl"] = url,
            ["PeerOnQDeploymentEnvironment"] = environment,
        });

        Assert.Throws<InvalidOperationException>(() =>
            CloudEndpointConfiguration.ResolveAccountPortalUri(assembly, allowDevelopment));
        // An invalid optional browser link cannot stop accountless services from starting.
        Assert.Null(CloudEndpointConfiguration.TryCreate(assembly, false));
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
