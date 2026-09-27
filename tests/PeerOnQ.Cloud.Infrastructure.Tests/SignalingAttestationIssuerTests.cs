using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Infrastructure.Security;
using PeerOnQ.Shared.Contracts.Security;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class SignalingAttestationIssuerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "peeronq-cloud-attestation",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Concurrent_issuance_produces_valid_unique_proof_bound_tokens()
    {
        Directory.CreateDirectory(_root);
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPath = Path.Combine(_root, "signing-key.pem");
        File.WriteAllText(privateKeyPath, signingKey.ExportPkcs8PrivateKeyPem());
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        using var issuer = new EcdsaSignalingAttestationIssuer(
            Options.Create(new SignalingAttestationIssuerOptions
            {
                Issuer = "https://identity.peeronq.test",
                Audience = "peeronq-signaling",
                Lifetime = TimeSpan.FromMinutes(5),
                PrivateKeyFile = privateKeyPath,
            }),
            new FixedTimeProvider(now));
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceSpki = deviceKey.ExportSubjectPublicKeyInfo();
        var deviceId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var request = new SignalingAttestationIssueRequest(
            "123-456-789-012",
            Convert.ToHexString(SHA256.HashData(deviceSpki)).ToLowerInvariant(),
            deviceId,
            installationId);

        var issued = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => issuer.Issue(request))));

        Assert.Equal(32, issued.Select(value => value.Token).Distinct(StringComparer.Ordinal).Count());
        var publicSpki = signingKey.ExportSubjectPublicKeyInfo();
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SignalingAttestationTokenV1.ComputeKeyId(publicSpki)] = publicSpki,
        };
        foreach (var value in issued)
        {
            Assert.Equal(now.AddMinutes(5), value.ExpiresAtUtc);
            Assert.True(SignalingAttestationTokenV1.TryValidate(
                value.Token,
                keys,
                "https://identity.peeronq.test",
                "peeronq-signaling",
                now,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(5),
                out var claims,
                out var error));
            Assert.Equal(SignalingAttestationValidationError.None, error);
            Assert.Equal(request.PublicDeviceId, claims!.PublicDeviceId);
            Assert.Equal(deviceId, claims.DeviceId);
            Assert.Equal(installationId, claims.InstallationId);
            Assert.Equal(request.SpkiSha256, claims.SpkiSha256);
        }
    }

    [Fact]
    public void Missing_private_key_configuration_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new EcdsaSignalingAttestationIssuer(
                Options.Create(new SignalingAttestationIssuerOptions
                {
                    Issuer = "https://identity.peeronq.test",
                    Audience = "peeronq-signaling",
                    PrivateKeyFile = Path.Combine(_root, "missing.pem"),
                }),
                TimeProvider.System));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
