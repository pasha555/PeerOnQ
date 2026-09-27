using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Services;

public sealed class SessionTelemetryService(
    ISessionRepository sessions,
    IDeviceRepository devices,
    IPublicDeviceIdService publicDeviceIds,
    ICloudUnitOfWork unitOfWork,
    CloudSecurityOptions options,
    TimeProvider? timeProvider = null) : ISessionTelemetryService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task RecordClientLifecycleAsync(DeviceAccessPrincipal caller, ClientSessionLifecycleEventV1 request, CancellationToken cancellationToken = default)
    {
        if (request.EventId == Guid.Empty)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Session event ID is required.");
        Device? peer = null;
        foreach (var peerHash in publicDeviceIds.ComputeLookupHashes(request.PeerPublicDeviceId))
        {
            peer = await devices.FindByPublicIdHashAsync(peerHash, cancellationToken);
            if (peer is not null) break;
        }
        if (peer is null || peer.IsRevoked)
            throw new CloudServiceException(CloudErrorCodes.DeviceRevoked, "The peer device is unavailable.");
        if (peer.Id == caller.DeviceId)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "A device cannot report itself as its session peer.");

        var viewerId = request.Role == SessionParticipantRoleV1.Viewer ? caller.DeviceId : peer.Id;
        var hostId = request.Role == SessionParticipantRoleV1.Host ? caller.DeviceId : peer.Id;
        var session = await sessions.FindByIdAsync(request.SessionId, cancellationToken);

        if (session is null)
        {
            if (request.Kind != SessionEventKindV1.Started)
                throw new CloudServiceException(CloudErrorCodes.SessionNotFound, "The session must be started before later lifecycle events.");
            session = RemoteSession.Start(request.SessionId, viewerId, hostId, request.PermissionMode.ToDomain(),
                request.ServerRegion,
                request.Role == SessionParticipantRoleV1.Viewer ? request.ClientVersion : null,
                request.Role == SessionParticipantRoleV1.Host ? request.ClientVersion : null,
                request.StartedAtUtc,
                _time.GetUtcNow());
            sessions.Add(session);
        }
        else
        {
            if (session.ViewerDeviceId != viewerId || session.HostDeviceId != hostId)
                throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Session participants do not match the authenticated reporter.");
            session.ReportParticipant(caller.DeviceId, request.ClientVersion);
        }

        if (request.Kind == SessionEventKindV1.Connected && session.Lifecycle == SessionLifecycle.Starting)
            session.MarkConnected(request.ConnectionPath.ToDomain(), request.UsedTurn, request.OccurredAtUtc, _time.GetUtcNow());
        else if (request.Kind == SessionEventKindV1.Ended && session.Lifecycle is not (SessionLifecycle.Ended or SessionLifecycle.Failed))
        {
            session.End((request.EndReason ?? SessionEndReasonV1.Error).ToDomain(), request.FailureStage,
                request.FailureCode, request.ReconnectCount, request.OccurredAtUtc);
            if (!string.IsNullOrWhiteSpace(request.FailureCode))
                sessions.AddFailure(new SessionFailure(request.EventId, request.SessionId,
                    request.FailureStage ?? "Unknown", request.FailureCode, request.OccurredAtUtc));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<SessionHeartbeatResultV1> HeartbeatAsync(
        DeviceAccessPrincipal caller,
        ClientSessionHeartbeatV1 request,
        CancellationToken cancellationToken = default)
    {
        if (request.EventId == Guid.Empty || request.SessionId == Guid.Empty)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Session heartbeat identity is required.");

        var now = _time.GetUtcNow();
        if ((request.SentAtUtc - now).Duration() > options.MaximumClientClockSkew)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest,
                "Session heartbeat timestamp is outside the allowed clock skew.");

        var session = await sessions.FindByIdAsync(request.SessionId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.SessionNotFound, "Session not found.");
        if (caller.DeviceId != session.ViewerDeviceId && caller.DeviceId != session.HostDeviceId)
            throw new CloudServiceException(CloudErrorCodes.SessionNotFound, "Session not found.");

        if (session.Lifecycle is SessionLifecycle.Ended or SessionLifecycle.Failed or SessionLifecycle.Stale)
            return new SessionHeartbeatResultV1(session.Id, session.LastActivityAtUtc, IsDuplicate: true);
        if (session.Lifecycle != SessionLifecycle.Connected)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest,
                "Only a connected session accepts heartbeats.");

        var updated = session.RecordActivity(caller.DeviceId, request.EventId, now);
        if (updated) await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SessionHeartbeatResultV1(session.Id, session.LastActivityAtUtc, IsDuplicate: !updated);
    }

    public async Task StartAsync(SessionStartedEventV1 request, CancellationToken cancellationToken = default)
    {
        var existing = await sessions.FindByIdAsync(request.SessionId, cancellationToken);
        if (existing is not null)
        {
            if (existing.ViewerDeviceId == request.ViewerDeviceId && existing.HostDeviceId == request.HostDeviceId) return;
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Session ID is already in use.");
        }
        if (await devices.FindByIdAsync(request.ViewerDeviceId, cancellationToken) is not { IsRevoked: false } ||
            await devices.FindByIdAsync(request.HostDeviceId, cancellationToken) is not { IsRevoked: false })
            throw new CloudServiceException(CloudErrorCodes.DeviceRevoked, "Both session devices must exist and be active.");

        sessions.Add(RemoteSession.Start(request.SessionId, request.ViewerDeviceId, request.HostDeviceId,
            request.PermissionMode.ToDomain(), request.ServerRegion, request.ClientVersionViewer,
            request.ClientVersionHost, request.StartedAtUtc, _time.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkConnectedAsync(SessionConnectedEventV1 request, CancellationToken cancellationToken = default)
    {
        var session = await sessions.FindByIdAsync(request.SessionId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.SessionNotFound, "Session not found.");
        if (session.Lifecycle == SessionLifecycle.Connected) return;
        session.MarkConnected(request.ConnectionPath.ToDomain(), request.UsedTurn, request.ConnectedAtUtc, _time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task EndAsync(SessionEndedEventV1 request, CancellationToken cancellationToken = default)
    {
        var session = await sessions.FindByIdAsync(request.SessionId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.SessionNotFound, "Session not found.");
        if (session.Lifecycle is SessionLifecycle.Ended or SessionLifecycle.Failed) return;
        session.End(request.EndReason.ToDomain(), request.FailureStage, request.FailureCode, request.ReconnectCount, request.EndedAtUtc);
        if (!string.IsNullOrWhiteSpace(request.FailureCode))
            sessions.AddFailure(new SessionFailure(Guid.NewGuid(), request.SessionId, request.FailureStage ?? "Unknown", request.FailureCode, request.EndedAtUtc));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> ReconcileStaleAsync(
        DateTimeOffset negotiationBeforeUtc,
        DateTimeOffset connectedBeforeUtc,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        var stale = await sessions.FindStaleActiveAsync(
            negotiationBeforeUtc,
            connectedBeforeUtc,
            maximumCount,
            cancellationToken);
        var now = _time.GetUtcNow();
        foreach (var session in stale) session.MarkStale(now);
        if (stale.Count > 0) await unitOfWork.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }
}
