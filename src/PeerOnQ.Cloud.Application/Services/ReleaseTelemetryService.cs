using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Services;

public sealed class ReleaseTelemetryService(
    IReleaseRepository releases,
    IInstallationRepository installations,
    ICloudUnitOfWork unitOfWork) : IReleaseTelemetryService
{
    public async Task RecordClientUpdateEventAsync(DeviceAccessPrincipal caller, ClientUpdateEventV1 request, CancellationToken cancellationToken = default)
    {
        if (request.EventId == Guid.Empty || string.IsNullOrWhiteSpace(request.Version) || request.Version.Length > 64)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Update event identity is invalid.");
        if (await releases.UpdateEventExistsAsync(request.EventId, cancellationToken)) return;
        var installation = await installations.FindByIdAsync(caller.InstallationId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.InstallationNotFound, "Installation not found.");
        if (installation.DeviceId != caller.DeviceId)
            throw new CloudServiceException(CloudErrorCodes.IdentityProofInvalid, "Authenticated device does not own the installation.");
        var release = await releases.FindByVersionAsync(request.Version, request.Channel.ToDomain(), request.Architecture.ToDomain(), cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Release not found.");
        releases.AddUpdateEvent(new UpdateEvent(request.EventId, installation.Id, release.Id,
            request.Kind.ToDomain(), request.FailureCode, request.OccurredAtUtc));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordUpdateEventAsync(UpdateEventRequestV1 request, CancellationToken cancellationToken = default)
    {
        if (await releases.UpdateEventExistsAsync(request.EventId, cancellationToken)) return;
        if (await installations.FindByIdAsync(request.InstallationId, cancellationToken) is null)
            throw new CloudServiceException(CloudErrorCodes.InstallationNotFound, "Installation not found.");
        if (await releases.FindByIdAsync(request.ReleaseId, cancellationToken) is null)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Release not found.");
        releases.AddUpdateEvent(new UpdateEvent(request.EventId, request.InstallationId, request.ReleaseId,
            request.Kind.ToDomain(), request.FailureCode, request.OccurredAtUtc));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
