using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using PeerOnQ.Infrastructure.Updates;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

[Collection("Update configuration environment")]
public sealed class UpdateConfigurationTests
{
    private static readonly object EnvironmentLock = new();

    [Fact]
    public void Complete_compiled_server_trust_enables_verified_updates()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = CreateManifest(new Dictionary<string, string>
        {
            ["PeerOnQUpdateManifestUrl"] = "https://updates.peeronq.com/beta/x64/manifest.json",
            ["PeerOnQUpdatePublicKeySpki"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            ["PeerOnQUpdateKeyId"] = "peeronq-release-2026",
            ["PeerOnQPublisherCertificateSha256"] = new string('A', 64),
            ["PeerOnQReleaseChannel"] = "beta",
        });

        var options = UpdateConfiguration.TryCreate(manifest, "updates", "device-rollout", false);

        Assert.NotNull(options);
        Assert.Equal("updates.peeronq.com", options.ManifestUri.Host);
        Assert.Equal(UpdateChannel.Beta, options.Channel);
        Assert.Contains(new string('A', 64), options.AllowedPublisherCertificateSha256);
    }

    [Fact]
    public void Incomplete_or_invalid_server_trust_fails_closed()
    {
        var incomplete = CreateManifest(new Dictionary<string, string>
        {
            ["PeerOnQUpdateManifestUrl"] = "https://updates.peeronq.com/beta/x64/manifest.json",
        });
        var invalidKey = CreateManifest(new Dictionary<string, string>
        {
            ["PeerOnQUpdateManifestUrl"] = "https://updates.peeronq.com/beta/x64/manifest.json",
            ["PeerOnQUpdatePublicKeySpki"] = Convert.ToBase64String([1, 2, 3]),
            ["PeerOnQUpdateKeyId"] = "peeronq-release-2026",
            ["PeerOnQPublisherCertificateSha256"] = new string('A', 64),
        });

        Assert.Throws<InvalidOperationException>(() =>
            UpdateConfiguration.TryCreate(incomplete, "updates", "device-rollout", false));
        Assert.Throws<InvalidOperationException>(() =>
            UpdateConfiguration.TryCreate(invalidKey, "updates", "device-rollout", false));
    }

    [Fact]
    public void Development_environment_can_supply_complete_public_trust_without_affecting_production()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var values = new Dictionary<string, string?>
        {
            ["PEERONQ_UPDATE_MANIFEST_URL"] = "https://updates.dev.localhost/beta/x64/manifest.json",
            ["PEERONQ_UPDATE_PUBLIC_KEY_SPKI"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            ["PEERONQ_UPDATE_KEY_ID"] = "peeronq-development-release",
            ["PEERONQ_PUBLISHER_CERTIFICATE_SHA256"] = new string('B', 64),
        };

        lock (EnvironmentLock)
        {
            var previous = values.Keys.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
            try
            {
                foreach (var (name, value) in values) Environment.SetEnvironmentVariable(name, value);
                var emptyManifest = CreateManifest(new Dictionary<string, string>());

                Assert.Null(UpdateConfiguration.TryCreate(emptyManifest, "updates", "device-rollout", false));
                Assert.NotNull(UpdateConfiguration.TryCreate(emptyManifest, "updates", "device-rollout", true));
            }
            finally
            {
                foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static Assembly CreateManifest(IReadOnlyDictionary<string, string> values)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"PeerOnQ.UpdateConfigurationTests.{Guid.NewGuid():N}") { Version = new Version(0, 9, 57, 0) },
            AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        foreach (var (key, value) in values)
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [key, value]));
        return assembly;
    }
}

[CollectionDefinition("Update configuration environment", DisableParallelization = true)]
public sealed class UpdateConfigurationEnvironmentCollection;
