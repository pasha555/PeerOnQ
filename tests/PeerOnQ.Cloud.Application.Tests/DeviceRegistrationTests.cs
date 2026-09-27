using System.Security.Cryptography;
using System.Text;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Application.Security;
using PeerOnQ.Cloud.Application.Services;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Tests;

public sealed class DeviceRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ChallengeBindsKeyAndCompleteMetadataWithoutPersistingRows()
    {
        var fixture = CreateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var challenge = await fixture.Service.IssueChallengeAsync(Request(Guid.NewGuid(), key));

        Assert.Empty(fixture.Store.Devices);
        Assert.Empty(fixture.Store.Installations);
        Assert.Contains("audience=peeronq-cloud", challenge.CanonicalPayload, StringComparison.Ordinal);
        Assert.Contains("purpose=device-authentication", challenge.CanonicalPayload, StringComparison.Ordinal);
        Assert.Contains("key_fingerprint=", challenge.CanonicalPayload, StringComparison.Ordinal);
        Assert.Contains("platform=Windows", challenge.CanonicalPayload, StringComparison.Ordinal);
        Assert.Contains("architecture=X64", challenge.CanonicalPayload, StringComparison.Ordinal);
        Assert.Contains("app_version_sha256=", challenge.CanonicalPayload, StringComparison.Ordinal);
        Assert.Contains("os_version_sha256=", challenge.CanonicalPayload, StringComparison.Ordinal);
        Assert.Contains("install_channel=Stable", challenge.CanonicalPayload, StringComparison.Ordinal);
        Assert.Contains("region_sha256=", challenge.CanonicalPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("public_id", challenge.CanonicalPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Now.AddSeconds(90), challenge.ExpiresAtUtc);
    }

    [Fact]
    public async Task ProofCreatesRowsThenReplayIsRejected()
    {
        var fixture = CreateFixture();
        var installationId = Guid.NewGuid();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challenge = await fixture.Service.IssueChallengeAsync(Request(installationId, key));
        var authentication = Authentication(challenge, installationId, key);

        var result = await fixture.Service.AuthenticateAsync(authentication);

        Assert.NotEmpty(result.AccessToken);
        Assert.StartsWith("test-attestation:", result.SignalingAttestation, StringComparison.Ordinal);
        Assert.Equal(Now.AddMinutes(5), result.SignalingAttestationExpiresAtUtc);
        Assert.Matches("^[0-9]{3}(?:-[0-9]{3}){3}$", result.PublicDeviceId);
        var device = Assert.Single(fixture.Store.Devices);
        var installation = Assert.Single(fixture.Store.Installations);
        Assert.Equal(result.DeviceId, device.Id);
        Assert.Equal(result.DeviceId, installation.DeviceId);
        Assert.Equal(1, installation.ProofBindingVersion);
        Assert.DoesNotContain(result.PublicDeviceId, device.MaskedPublicDeviceId, StringComparison.Ordinal);

        var replay = await Assert.ThrowsAsync<CloudServiceException>(() =>
            fixture.Service.AuthenticateAsync(authentication));
        Assert.Equal(CloudErrorCodes.ChallengeInvalidOrExpired, replay.Code);
    }

    [Fact]
    public async Task ExpiredChallengeIsRejectedWithoutRows()
    {
        var fixture = CreateFixture();
        var installationId = Guid.NewGuid();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challenge = await fixture.Service.IssueChallengeAsync(Request(installationId, key));
        fixture.Time.UtcNow = Now.AddMinutes(3);

        var exception = await Assert.ThrowsAsync<CloudServiceException>(() =>
            fixture.Service.AuthenticateAsync(Authentication(challenge, installationId, key)));

        Assert.Equal(CloudErrorCodes.ChallengeInvalidOrExpired, exception.Code);
        Assert.Empty(fixture.Store.Devices);
        Assert.Empty(fixture.Store.Installations);
    }

    [Fact]
    public async Task SameFingerprintReusesStableAliasAcrossInstallations()
    {
        var fixture = CreateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        var first = await AuthenticateAsync(fixture.Service, firstId, key);
        var second = await AuthenticateAsync(fixture.Service, secondId, key);

        Assert.Equal(first.PublicDeviceId, second.PublicDeviceId);
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Single(fixture.Store.Devices);
        Assert.Equal(2, fixture.Store.Installations.Count);
    }

    [Fact]
    public async Task DifferentKeyCannotTakeProofBoundInstallation()
    {
        var fixture = CreateFixture();
        var installationId = Guid.NewGuid();
        using var original = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var clone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await AuthenticateAsync(fixture.Service, installationId, original);

        var challenge = await fixture.Service.IssueChallengeAsync(Request(installationId, clone));
        var conflict = await Assert.ThrowsAsync<CloudServiceException>(() =>
            fixture.Service.AuthenticateAsync(Authentication(challenge, installationId, clone)));

        Assert.Equal(CloudErrorCodes.InstallationIdentityConflict, conflict.Code);
        Assert.Single(fixture.Store.Devices);
        Assert.Single(fixture.Store.Installations);
    }

    [Fact]
    public async Task AttackerCannotSelectVictimAlias()
    {
        Assert.DoesNotContain(typeof(DeviceRegistrationRequestV1).GetProperties(),
            property => property.Name.Contains("PublicDeviceId", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(DeviceAuthenticationRequestV1).GetProperties(),
            property => property.Name.Contains("PublicDeviceId", StringComparison.Ordinal));

        var fixture = CreateFixture();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var victim = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var attackerResult = await AuthenticateAsync(fixture.Service, Guid.NewGuid(), attacker);
        var victimResult = await AuthenticateAsync(fixture.Service, Guid.NewGuid(), victim);
        var verifier = new EcdsaDeviceProofVerifier();
        var attackerFingerprint = verifier.GetFingerprint(Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo()))!;
        var victimFingerprint = verifier.GetFingerprint(Convert.ToBase64String(victim.ExportSubjectPublicKeyInfo()))!;

        Assert.Equal(fixture.Ids.DeriveFromFingerprint(attackerFingerprint, 0).Value,
            attackerResult.PublicDeviceId);
        Assert.Equal(fixture.Ids.DeriveFromFingerprint(victimFingerprint, 0).Value,
            victimResult.PublicDeviceId);
        Assert.NotEqual(attackerResult.PublicDeviceId, victimResult.PublicDeviceId);
    }

    [Fact]
    public async Task AliasCollisionAdvancesStoredCounter()
    {
        var fixture = CreateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new EcdsaDeviceProofVerifier();
        var fingerprint = verifier.GetFingerprint(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))!;
        var occupied = fixture.Ids.DeriveFromFingerprint(fingerprint, 0);
        fixture.Store.Devices.Add(Device.Create(occupied.LookupHash, occupied.MaskedValue, "Existing",
            new string('f', 64), occupied.CollisionCounter, occupied.KeyVersion, Now));

        var result = await AuthenticateAsync(fixture.Service, Guid.NewGuid(), key);

        Assert.Equal(fixture.Ids.DeriveFromFingerprint(fingerprint, 1).Value, result.PublicDeviceId);
        Assert.Equal(1, fixture.Store.Devices.Single(value => value.Id == result.DeviceId)
            .PublicDeviceIdCollisionCounter);
    }

    [Fact]
    public async Task UnsupportedVersionIsRejectedBeforeChallengeStorageOrRows()
    {
        var fixture = CreateFixture();
        fixture.Store.MinimumSupportedVersion = "2.0.0";
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var error = await Assert.ThrowsAsync<CloudServiceException>(() =>
            fixture.Service.IssueChallengeAsync(Request(Guid.NewGuid(), key)));

        Assert.Equal(CloudErrorCodes.UnsupportedVersion, error.Code);
        Assert.Empty(fixture.Store.Devices);
        Assert.Empty(fixture.Store.Installations);
    }

    [Fact]
    public async Task AuthenticatedInstallationRegistrationIsIdempotentAndCannotCreate()
    {
        var fixture = CreateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installationId = Guid.NewGuid();
        var authenticated = await AuthenticateAsync(fixture.Service, installationId, key);
        var service = new InstallationService(fixture.Store, fixture.Store, fixture.Store,
            new CloudSecurityOptions(), fixture.Time);
        var request = InstallationRequest(installationId);

        var result = await service.RegisterAsync(new DeviceAccessPrincipal(authenticated.DeviceId,
            installationId, Now.AddMinutes(10)), request);

        Assert.False(result.IsNew);
        Assert.Single(fixture.Store.Installations);
        var denied = await Assert.ThrowsAsync<CloudServiceException>(() => service.RegisterAsync(
            new DeviceAccessPrincipal(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(10)),
            request with { InstallationId = Guid.NewGuid() }));
        Assert.Equal(CloudErrorCodes.InstallationIdentityConflict, denied.Code);
        Assert.Single(fixture.Store.Installations);
    }

    [Fact]
    public void KeyRotationRetainsStableAliasAndLookupWhilePreviousKeyIsConfigured()
    {
        var oldKey = Enumerable.Repeat((byte)3, 32).ToArray();
        var newKey = Enumerable.Repeat((byte)4, 32).ToArray();
        var oldService = new PublicDeviceIdService(1, oldKey, null);
        var fingerprint = new string('a', 64);
        var original = oldService.DeriveFromFingerprint(fingerprint, 7);
        var rotated = new PublicDeviceIdService(2, newKey,
            new Dictionary<int, byte[]> { [1] = oldKey });

        var reproduced = rotated.DeriveFromFingerprint(fingerprint, 7, keyVersion: 1);

        Assert.Equal(original.Value, reproduced.Value);
        Assert.Equal(original.LookupHash, reproduced.LookupHash);
        Assert.Contains(rotated.ComputeLookupHashes(original.Value),
            hash => hash.SequenceEqual(original.LookupHash));
        Assert.Throws<InvalidOperationException>(() =>
            new PublicDeviceIdService(2, newKey, null)
                .DeriveFromFingerprint(fingerprint, 7, keyVersion: 1));
    }

    private static async Task<DeviceAuthenticationResultV1> AuthenticateAsync(
        DeviceRegistrationService service,
        Guid installationId,
        ECDsa key)
    {
        var challenge = await service.IssueChallengeAsync(Request(installationId, key));
        return await service.AuthenticateAsync(Authentication(challenge, installationId, key));
    }

    private static DeviceRegistrationRequestV1 Request(Guid installationId, ECDsa key) => new(
        installationId,
        "Office PC",
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        PlatformKindV1.Windows,
        ArchitectureKindV1.X64,
        "1.0.0",
        "Windows 11",
        InstallChannelV1.Stable,
        "eu-west",
        "1");

    private static InstallationRegistrationRequestV1 InstallationRequest(Guid installationId) => new(
        installationId,
        PlatformKindV1.Windows,
        ArchitectureKindV1.X64,
        "1.0.0",
        "Windows 11",
        InstallChannelV1.Stable,
        "1",
        "eu-west");

    private static DeviceAuthenticationRequestV1 Authentication(
        DeviceRegistrationChallengeV1 challenge,
        Guid installationId,
        ECDsa key)
    {
        var signature = key.SignData(Encoding.UTF8.GetBytes(challenge.CanonicalPayload),
            HashAlgorithmName.SHA256);
        return new DeviceAuthenticationRequestV1(challenge.ChallengeId,
            Convert.ToBase64String(signature),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            installationId);
    }

    private static Fixture CreateFixture()
    {
        var time = new ManualTimeProvider(Now);
        var ids = new PublicDeviceIdService(Enumerable.Repeat((byte)7, 32).ToArray());
        var store = new TestStore { PublicDeviceIds = ids };
        var service = new DeviceRegistrationService(
            new MemoryChallengeStore(),
            new EcdsaDeviceProofVerifier(),
            new MemoryTokenIssuer(time),
            new MemorySignalingAttestationIssuer(time),
            store,
            store,
            new CloudSecurityOptions(),
            time);
        return new Fixture(service, store, ids, time);
    }

    private sealed record Fixture(
        DeviceRegistrationService Service,
        TestStore Store,
        PublicDeviceIdService Ids,
        ManualTimeProvider Time);
}
