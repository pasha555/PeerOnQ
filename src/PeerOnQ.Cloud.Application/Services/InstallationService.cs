using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Services;

public sealed class InstallationService(
    IInstallationRepository installations,
    IReleaseRepository releases,
    ICloudUnitOfWork unitOfWork,
    CloudSecurityOptions options,
    TimeProvider? timeProvider = null) : IInstallationService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<InstallationRegistrationResultV1> RegisterAsync(
        DeviceAccessPrincipal caller,
        InstallationRegistrationRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (request.InstallationId == Guid.Empty || caller.InstallationId != request.InstallationId)
            throw new CloudServiceException(CloudErrorCodes.InstallationIdentityConflict,
                "The authenticated token does not own this installation.");
        ClientRegistrationValidation.ValidateProtocol(options, request.ProtocolVersion);
        var appVersion = ClientRegistrationValidation.Normalize(request.AppVersion, 64, "Application version");
        var osVersion = ClientRegistrationValidation.Normalize(request.OsVersion, 128, "Operating-system version");
        var region = ClientRegistrationValidation.Normalize(request.Region, 64, "Region");
        var platform = request.Platform.ToDomain();
        var architecture = request.Architecture.ToDomain();
        var channel = request.InstallChannel.ToDomain();
        var minimumVersion = await releases.FindMinimumSupportedVersionAsync(channel, architecture, cancellationToken);
        ClientRegistrationValidation.EnsureSupportedVersion(appVersion, minimumVersion);

        var installation = await installations.FindByIdAsync(request.InstallationId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.InstallationNotFound,
                "Installation must be created by a successful device proof.");
        if (installation.DeviceId != caller.DeviceId || installation.ProofBindingVersion < 1)
            throw new CloudServiceException(CloudErrorCodes.InstallationIdentityConflict,
                "The installation is not proof-bound to the authenticated device.");
        if (installation.IsBlocked)
            throw new CloudServiceException(CloudErrorCodes.InstallationBlocked, "The installation is blocked.");
        if (installation.Platform != platform || installation.Architecture != architecture)
            throw new CloudServiceException(CloudErrorCodes.InstallationIdentityConflict,
                "Installation platform and architecture cannot change.");

        var now = _time.GetUtcNow();
        installation.UpdateVersion(appVersion, osVersion, architecture, channel, now);
        installation.Heartbeat(appVersion, region, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new InstallationRegistrationResultV1(
            installation.Id,
            installation.CreatedAtUtc,
            IsNew: false,
            installation.IsBlocked,
            minimumVersion);
    }

    public async Task<InstallationHeartbeatResultV1> HeartbeatAsync(
        InstallationHeartbeatRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        if ((request.SentAtUtc - now).Duration() > options.MaximumClientClockSkew)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest,
                "Heartbeat timestamp is outside the allowed clock skew.");

        var installation = await installations.FindByIdAsync(request.InstallationId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.InstallationNotFound, "Installation not found.");
        if (installation.IsBlocked)
            throw new CloudServiceException(CloudErrorCodes.InstallationBlocked, "The installation is blocked.");
        installation.Heartbeat(request.AppVersion, request.Region, now);
        var minimum = await releases.FindMinimumSupportedVersionAsync(
            installation.InstallChannel, installation.Architecture, cancellationToken);
        ClientRegistrationValidation.EnsureSupportedVersion(request.AppVersion, minimum);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new InstallationHeartbeatResultV1(now, options.HeartbeatIntervalSeconds,
            installation.IsBlocked, minimum);
    }

    public async Task UpdateVersionAsync(
        InstallationVersionRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        var installation = await installations.FindByIdAsync(request.InstallationId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.InstallationNotFound, "Installation not found.");
        var minimum = await releases.FindMinimumSupportedVersionAsync(
            request.InstallChannel.ToDomain(), request.Architecture.ToDomain(), cancellationToken);
        ClientRegistrationValidation.EnsureSupportedVersion(request.AppVersion, minimum);
        installation.UpdateVersion(request.AppVersion, request.OsVersion,
            request.Architecture.ToDomain(), request.InstallChannel.ToDomain(), _time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task UnregisterAsync(
        InstallationUnregisterRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest,
                "An unregister reason is required.");
        var installation = await installations.FindByIdAsync(request.InstallationId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.InstallationNotFound, "Installation not found.");
        installation.Unregister(_time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
