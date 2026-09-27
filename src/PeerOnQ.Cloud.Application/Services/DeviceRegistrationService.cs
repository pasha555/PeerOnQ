using System.Security.Cryptography;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Application.Security;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Services;

public sealed class DeviceRegistrationService(
    IDeviceChallengeStore challengeStore,
    IDeviceProofVerifier proofVerifier,
    IDeviceAccessTokenIssuer accessTokenIssuer,
    ISignalingAttestationIssuer signalingAttestations,
    IDeviceBootstrapStore bootstrapStore,
    IReleaseRepository releases,
    CloudSecurityOptions options,
    TimeProvider? timeProvider = null) : IDeviceRegistrationService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<DeviceRegistrationChallengeV1> IssueChallengeAsync(
        DeviceRegistrationRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (request.InstallationId == Guid.Empty)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Installation ID is required.");
        ClientRegistrationValidation.ValidateProtocol(options, request.ProtocolVersion);

        var fingerprint = proofVerifier.GetFingerprint(request.PublicKeySpkiBase64)
            ?? throw new CloudServiceException(CloudErrorCodes.IdentityProofInvalid,
                "The device public key is invalid.");
        var displayName = ClientRegistrationValidation.Normalize(request.DisplayName, 128, "Display name");
        var appVersion = ClientRegistrationValidation.Normalize(request.AppVersion, 64, "Application version");
        var osVersion = ClientRegistrationValidation.Normalize(request.OsVersion, 128, "Operating-system version");
        var region = ClientRegistrationValidation.Normalize(request.Region, 64, "Region");
        var architecture = request.Architecture.ToDomain();
        var installChannel = request.InstallChannel.ToDomain();
        _ = request.Platform.ToDomain();
        var minimumVersion = await releases.FindMinimumSupportedVersionAsync(
            installChannel, architecture, cancellationToken);
        ClientRegistrationValidation.EnsureSupportedVersion(appVersion, minimumVersion);

        var now = _time.GetUtcNow();
        var expires = now + options.ChallengeLifetime;
        var (challengeId, payload) = CanonicalDeviceChallenge.Create(
            options.ChallengeAudience,
            options.ChallengePurpose,
            request.InstallationId,
            fingerprint,
            displayName,
            request.Platform,
            request.Architecture,
            appVersion,
            osVersion,
            request.InstallChannel,
            region,
            request.ProtocolVersion,
            now,
            expires);

        await challengeStore.StoreAsync(new DeviceChallengeRecord(
            challengeId,
            request.InstallationId,
            fingerprint,
            displayName,
            request.Platform,
            request.Architecture,
            appVersion,
            osVersion,
            request.InstallChannel,
            region,
            request.ProtocolVersion,
            payload,
            now,
            expires), cancellationToken);
        return new DeviceRegistrationChallengeV1(challengeId, payload, expires);
    }

    public async Task<DeviceAuthenticationResultV1> AuthenticateAsync(
        DeviceAuthenticationRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(request.ChallengeId) || request.ChallengeId.Length > 128)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Challenge ID is invalid.");

        // One use even for a failed signature: an attacker cannot probe one challenge repeatedly.
        var challenge = await challengeStore.ConsumeAsync(request.ChallengeId, cancellationToken);
        var now = _time.GetUtcNow();
        if (challenge is null || challenge.ExpiresAtUtc <= now ||
            challenge.InstallationId != request.InstallationId)
        {
            throw new CloudServiceException(CloudErrorCodes.ChallengeInvalidOrExpired,
                "The authentication challenge is invalid or expired.");
        }

        var presentedFingerprint = proofVerifier.GetFingerprint(request.PublicKeySpkiBase64);
        if (presentedFingerprint is null || !FixedTimeFingerprintEquals(
                challenge.IdentityFingerprint, presentedFingerprint))
        {
            throw new CloudServiceException(CloudErrorCodes.IdentityProofInvalid,
                "The device identity proof is invalid.");
        }

        var proof = proofVerifier.Verify(
            request.PublicKeySpkiBase64,
            challenge.CanonicalPayload,
            request.SignatureBase64);
        if (!proof.IsValid || proof.Fingerprint is null ||
            !FixedTimeFingerprintEquals(challenge.IdentityFingerprint, proof.Fingerprint))
        {
            throw new CloudServiceException(CloudErrorCodes.IdentityProofInvalid,
                "The device identity proof is invalid.");
        }

        var architecture = challenge.Architecture.ToDomain();
        var installChannel = challenge.InstallChannel.ToDomain();
        var minimumVersion = await releases.FindMinimumSupportedVersionAsync(
            installChannel, architecture, cancellationToken);
        ClientRegistrationValidation.EnsureSupportedVersion(challenge.AppVersion, minimumVersion);

        var completed = await bootstrapStore.CompleteAsync(new DeviceBootstrapRequest(
            challenge.InstallationId,
            challenge.IdentityFingerprint,
            challenge.DisplayName,
            challenge.Platform.ToDomain(),
            architecture,
            challenge.AppVersion,
            challenge.OsVersion,
            installChannel,
            challenge.ProtocolVersion,
            challenge.Region,
            now), cancellationToken);

        ClientRegistrationValidation.EnsureSupportedVersion(
            challenge.AppVersion,
            string.IsNullOrWhiteSpace(completed.ManagedPolicy?.MinimumClientVersion)
                ? null
                : completed.ManagedPolicy.MinimumClientVersion);

        var signalingAttestation = signalingAttestations.Issue(new SignalingAttestationIssueRequest(
            completed.PublicDeviceId,
            challenge.IdentityFingerprint,
            completed.DeviceId,
            completed.InstallationId,
            completed.ManagedPolicy));

        var token = await accessTokenIssuer.IssueAsync(
            completed.DeviceId,
            completed.InstallationId,
            options.DeviceAccessTokenLifetime,
            cancellationToken);
        return new DeviceAuthenticationResultV1(
            token.Token,
            token.ExpiresAtUtc,
            completed.DeviceId,
            completed.InstallationId,
            completed.PublicDeviceId,
            signalingAttestation.Token,
            signalingAttestation.ExpiresAtUtc);
    }

    private static bool FixedTimeFingerprintEquals(string expected, string actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(actual));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
