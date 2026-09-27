using System.Security.Cryptography;
using PeerOnQ.Domain.Errors;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Signaling.Server;
using PeerOnQ.Signaling.Server.Security;
using Microsoft.Extensions.Options;
using PeerOnQ.Shared.Contracts.Security;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class SignalingAttestationTests : IDisposable
{
    private const string Issuer = "https://identity.peeronq.test";
    private const string Audience = "peeronq-signaling";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "peeronq-signaling-attestation",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Valid_cloud_attestation_registers_with_fresh_device_proof()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyFile = WritePublicKey(signer, "current.pem");
        await using var server = await StartAttestedServerAsync([publicKeyFile]);
        using var device = new TestDevice("Attested device");
        device.SignalingAttestation = Issue(signer, device);
        await using var client = device.CreateClient(server.WebSocketUri);

        await client.ConnectAsync(device.Identity);

        Assert.Equal(device.Id, client.RegisteredId);
    }

    [Fact]
    public async Task Stolen_attestation_without_the_bound_private_key_is_rejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyFile = WritePublicKey(signer, "current.pem");
        await using var server = await StartAttestedServerAsync([publicKeyFile]);
        using var legitimate = new TestDevice("Legitimate device");
        var stolenToken = Issue(signer, legitimate);
        using var attacker = legitimate.WithNewKeyPair();
        attacker.SignalingAttestation = stolenToken;
        await using var client = attacker.CreateClient(server.WebSocketUri);

        var error = await Assert.ThrowsAsync<SignalingAuthenticationException>(() =>
            client.ConnectAsync(attacker.Identity));

        Assert.Contains("invalid_attestation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_registration_fetches_the_latest_memory_only_attestation()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyFile = WritePublicKey(signer, "current.pem");
        await using var server = await StartAttestedServerAsync([publicKeyFile]);
        using var device = new TestDevice("Reconnect device");
        device.SignalingAttestation = Issue(signer, device);
        await using var client = device.CreateClient(server.WebSocketUri);

        await client.ConnectAsync(device.Identity);
        await client.DisconnectAsync();
        device.SignalingAttestation = Issue(signer, device);
        await client.ConnectAsync(device.Identity);

        Assert.Equal(2, device.SignalingAttestationRequests);
        Assert.Equal(device.Id, client.RegisteredId);
    }

    [Fact]
    public async Task Device_revocation_during_an_interruption_prevents_reauthentication()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyFile = WritePublicKey(signer, "current.pem");
        await using var server = await StartAttestedServerAsync([publicKeyFile]);
        using var device = new TestDevice("Revoked device");
        device.SignalingAttestation = Issue(signer, device);
        await using var client = device.CreateClient(server.WebSocketUri);

        await client.ConnectAsync(device.Identity);
        await client.DisconnectAsync();
        device.SignalingAttestation = "revoked";

        var error = await Assert.ThrowsAsync<SignalingAuthenticationException>(() =>
            client.ConnectAsync(device.Identity));

        Assert.Contains("invalid_attestation", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, device.SignalingAttestationRequests);
    }

    [Fact]
    public void Validator_rejects_alias_key_audience_signature_and_expiry_mismatches()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyFile = WritePublicKey(signer, "current.pem");
        var validator = CreateValidator([publicKeyFile]);
        using var device = new TestDevice("Bound device");
        using var otherKey = new TestDevice("Other key");
        var valid = Issue(signer, device);

        Assert.False(validator.TryValidate(
            valid, PeerOnQId.NewId(), device.Identity.PublicKey!, out var aliasError));
        Assert.Equal(SignalingAttestationValidationError.InvalidClaims, aliasError);

        Assert.False(validator.TryValidate(
            valid, device.Id, otherKey.Identity.PublicKey!, out var keyError));
        Assert.Equal(SignalingAttestationValidationError.InvalidClaims, keyError);

        var wrongAudience = Issue(signer, device, audience: "another-signaling-service");
        Assert.False(validator.TryValidate(
            wrongAudience, device.Id, device.Identity.PublicKey!, out var audienceError));
        Assert.Equal(SignalingAttestationValidationError.InvalidAudience, audienceError);

        var signatureStart = valid.LastIndexOf('.') + 1;
        var tampered = valid[..signatureStart]
                       + (valid[signatureStart] == 'A' ? 'B' : 'A')
                       + valid[(signatureStart + 1)..];
        Assert.False(validator.TryValidate(
            tampered, device.Id, device.Identity.PublicKey!, out var signatureError));
        Assert.Equal(SignalingAttestationValidationError.InvalidSignature, signatureError);

        var now = DateTimeOffset.UtcNow;
        var expired = Issue(signer, device, issuedAt: now.AddMinutes(-10), expiresAt: now.AddMinutes(-5));
        Assert.False(validator.TryValidate(
            expired, device.Id, device.Identity.PublicKey!, out var expiryError));
        Assert.Equal(SignalingAttestationValidationError.Expired, expiryError);
    }

    [Fact]
    public void Validator_accepts_previous_public_key_during_rotation_overlap()
    {
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var previous = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var validator = CreateValidator([
            WritePublicKey(current, "current.pem"),
            WritePublicKey(previous, "previous.pem"),
        ]);
        using var device = new TestDevice("Rotated device");
        var token = Issue(previous, device);

        Assert.True(validator.TryValidate(
            token, device.Id, device.Identity.PublicKey!, out var error));
        Assert.Equal(SignalingAttestationValidationError.None, error);
    }

    [Fact]
    public void Validator_preserves_signed_organization_policy_claims()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var validator = CreateValidator([WritePublicKey(signer, "managed.pem")]);
        using var device = new TestDevice("Managed device");
        var organizationId = Guid.NewGuid();
        var flags = SignalingOrganizationPolicyFlags.ViewOnly | SignalingOrganizationPolicyFlags.Clipboard;
        var spki = Convert.FromBase64String(device.Identity.PublicKey!);
        var now = DateTimeOffset.UtcNow;
        var token = SignalingAttestationTokenV1.Issue(new SignalingAttestationClaimsV1(
            Issuer, Audience, device.Id.Display, Guid.NewGuid(), device.Identity.InternalId,
            Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
            SignalingAttestationTokenV1.ComputeKeyId(signer.ExportSubjectPublicKeyInfo()),
            now.ToUnixTimeSeconds(), now.AddMinutes(5).ToUnixTimeSeconds(),
            SignalingAttestationTokenV1.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            organizationId, flags, "0.7.0", "eu-west"), signer);

        Assert.True(validator.TryValidate(token, device.Id, device.Identity.PublicKey!, out var claims, out var error));
        Assert.Equal(SignalingAttestationValidationError.None, error);
        Assert.Equal(organizationId, claims!.OrganizationId);
        Assert.Equal(flags, claims.OrganizationPolicyFlags);
        Assert.Equal("eu-west", claims.ApprovedRelayRegionsCsv);
    }

    [Fact]
    public void Validator_enforces_version_size_clock_and_maximum_lifetime_bounds()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var validator = CreateValidator([WritePublicKey(signer, "current.pem")]);
        using var device = new TestDevice("Bounded token device");
        var valid = Issue(signer, device);

        var unsupportedVersion = "pqsa2" + valid[SignalingAttestationTokenV1.Prefix.Length..];
        Assert.False(validator.TryValidate(
            unsupportedVersion, device.Id, device.Identity.PublicKey!, out var versionError));
        Assert.Equal(SignalingAttestationValidationError.UnsupportedVersion, versionError);

        Assert.False(validator.TryValidate(
            new string('A', SignalingAttestationTokenV1.MaximumTokenCharacters + 1),
            device.Id,
            device.Identity.PublicKey!,
            out var sizeError));
        Assert.Equal(SignalingAttestationValidationError.Malformed, sizeError);

        var now = DateTimeOffset.UtcNow;
        var future = Issue(signer, device, issuedAt: now.AddMinutes(5), expiresAt: now.AddMinutes(10));
        Assert.False(validator.TryValidate(
            future, device.Id, device.Identity.PublicKey!, out var futureError));
        Assert.Equal(SignalingAttestationValidationError.NotYetValid, futureError);

        var overlong = Issue(signer, device, issuedAt: now, expiresAt: now.AddMinutes(16));
        Assert.False(validator.TryValidate(
            overlong, device.Id, device.Identity.PublicKey!, out var lifetimeError));
        Assert.Equal(SignalingAttestationValidationError.LifetimeExceeded, lifetimeError);
    }

    [Fact]
    public async Task Staging_fails_closed_when_attestation_key_configuration_is_missing()
    {
        Assert.Throws<OptionsValidationException>(() =>
            SignalingApp.Create(["--environment", "Staging"], options =>
            {
                options.Attestation.Required = true;
                options.Attestation.AllowDevelopmentTofuFallback = false;
                options.Attestation.Issuer = Issuer;
                options.Attestation.Audience = Audience;
                options.Attestation.PublicKeyFiles = [];
            }));
        await Task.CompletedTask;
    }

    [Fact]
    public void Signaling_rejects_a_cloud_private_key_mounted_as_a_verification_key()
    {
        Directory.CreateDirectory(_root);
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyFile = Path.Combine(_root, "must-not-load.pem");
        File.WriteAllText(privateKeyFile, privateKey.ExportPkcs8PrivateKeyPem());

        Assert.False(CloudSignalingAttestationValidator.CanLoadPublicKeys([privateKeyFile]));
    }

    [Fact]
    public async Task Testing_tofu_fallback_remains_explicit_and_functional()
    {
        await using var server = await SignalingHarness.StartAsync();
        using var device = new TestDevice("Development fallback");
        await using var client = device.CreateClient(server.WebSocketUri);

        await client.ConnectAsync(device.Identity);

        Assert.Equal(device.Id, client.RegisteredId);
    }

    private Task<SignalingHarness> StartAttestedServerAsync(string[] publicKeyFiles) =>
        SignalingHarness.StartAsync(options =>
        {
            options.Attestation.Required = true;
            options.Attestation.AllowDevelopmentTofuFallback = false;
            options.Attestation.Issuer = Issuer;
            options.Attestation.Audience = Audience;
            options.Attestation.PublicKeyFiles = publicKeyFiles;
            options.Attestation.MaximumTokenLifetime = TimeSpan.FromMinutes(15);
            options.Attestation.ClockSkew = TimeSpan.FromSeconds(30);
        });

    private CloudSignalingAttestationValidator CreateValidator(string[] publicKeyFiles)
    {
        var options = new SignalingOptions();
        options.Attestation.Required = true;
        options.Attestation.Issuer = Issuer;
        options.Attestation.Audience = Audience;
        options.Attestation.PublicKeyFiles = publicKeyFiles;
        return new CloudSignalingAttestationValidator(Options.Create(options), TimeProvider.System);
    }

    private string WritePublicKey(ECDsa key, string fileName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, key.ExportSubjectPublicKeyInfoPem());
        return path;
    }

    private static string Issue(
        ECDsa signer,
        TestDevice device,
        string audience = Audience,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null)
    {
        var spki = Convert.FromBase64String(device.Identity.PublicKey!);
        var now = issuedAt ?? DateTimeOffset.UtcNow;
        var expiry = expiresAt ?? now.AddMinutes(5);
        return SignalingAttestationTokenV1.Issue(
            new SignalingAttestationClaimsV1(
                Issuer,
                audience,
                device.Id.Display,
                Guid.NewGuid(),
                device.Identity.InternalId,
                Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                SignalingAttestationTokenV1.ComputeKeyId(signer.ExportSubjectPublicKeyInfo()),
                now.ToUnixTimeSeconds(),
                expiry.ToUnixTimeSeconds(),
                SignalingAttestationTokenV1.Base64UrlEncode(RandomNumberGenerator.GetBytes(16))),
            signer);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
