using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Services;

public sealed class DownloadTrackingService(
    IDownloadRepository downloads,
    IDownloadArtifactRepository artifacts,
    IPrivacyHasher privacyHasher,
    ICloudUnitOfWork unitOfWork,
    TimeProvider? timeProvider = null) : IDownloadTrackingService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<DownloadStartResultV1> StartAsync(DownloadStartRequestV1 request, string? privacyScopedUniquenessValue, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 256)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "A bounded idempotency key is required.");
        var idempotencyHash = privacyHasher.ComputeHash("download-idempotency", request.IdempotencyKey);
        var existing = await downloads.FindByIdempotencyHashAsync(idempotencyHash, cancellationToken);
        if (existing is not null) return new DownloadStartResultV1(existing.Id, existing.StartedAtUtc, true);
        if (await downloads.FindByIdAsync(request.DownloadId, cancellationToken) is not null)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Download ID is already in use.");

        var now = _time.GetUtcNow();
        var uniqueHash = string.IsNullOrWhiteSpace(privacyScopedUniquenessValue)
            ? null
            : privacyHasher.ComputeHash($"download-unique:{now:yyyyMMdd}", privacyScopedUniquenessValue);
        var download = DownloadEvent.Start(request.DownloadId, idempotencyHash, uniqueHash,
            request.Platform.ToDomain(), request.Architecture.ToDomain(), request.Version,
            request.Channel.ToDomain(), request.Source, request.Campaign, request.CountryCode,
            request.UserAgentFamily, now);
        downloads.Add(download);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DownloadStartResultV1(download.Id, download.StartedAtUtc, false);
    }

    public async Task CompleteAsync(DownloadCompleteRequestV1 request, CancellationToken cancellationToken = default)
    {
        var download = await downloads.FindByIdAsync(request.DownloadId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.DownloadNotFound, "Download not found.");
        download.Complete(request.Result.ToDomain(), request.CompletedAtUtc);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public Task<DownloadArtifact?> ResolveLatestAsync(PlatformKindV1 platform, ArchitectureKindV1 architecture, InstallChannelV1 channel, CancellationToken cancellationToken = default) =>
        artifacts.ResolveLatestAsync(platform.ToDomain(), architecture.ToDomain(), channel.ToDomain(), cancellationToken);

    public Task<DownloadArtifact?> ResolveVersionAsync(PlatformKindV1 platform, ArchitectureKindV1 architecture, string version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Release version is invalid.");
        return artifacts.ResolveVersionAsync(platform.ToDomain(), architecture.ToDomain(), version, cancellationToken);
    }
}
