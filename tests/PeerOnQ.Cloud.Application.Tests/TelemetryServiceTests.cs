using System.Security.Cryptography;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Application.Security;
using PeerOnQ.Cloud.Application.Services;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Tests;

public sealed class TelemetryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DownloadStartIsIdempotentAndDoesNotStoreRawUniquenessInput()
    {
        var store = new TestStore();
        var ids = new PublicDeviceIdService(RandomNumberGenerator.GetBytes(32));
        var service = new DownloadTrackingService(store, new EmptyArtifactRepository(), ids, store, new ManualTimeProvider(Now));
        var request = new DownloadStartRequestV1(Guid.NewGuid(), PlatformKindV1.Windows, ArchitectureKindV1.X64,
            "1.0.0", InstallChannelV1.Stable, "website", "summer", "AZ", "Firefox", "idem-1");

        var first = await service.StartAsync(request, "ephemeral-address-bucket");
        var duplicate = await service.StartAsync(request with { DownloadId = Guid.NewGuid() }, "ephemeral-address-bucket");

        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.DownloadId, duplicate.DownloadId);
        Assert.Single(store.Downloads);
        Assert.Equal(32, store.Downloads[0].UniquenessKeyHash?.Length);
    }

    [Fact]
    public async Task PartialDownloadCompletionRemainsDistinctFromCompletedAnalytics()
    {
        var store = new TestStore();
        var ids = new PublicDeviceIdService(RandomNumberGenerator.GetBytes(32));
        var service = new DownloadTrackingService(store, new EmptyArtifactRepository(), ids, store, new ManualTimeProvider(Now));
        var downloadId = Guid.NewGuid();
        await service.StartAsync(new DownloadStartRequestV1(downloadId, PlatformKindV1.Windows,
            ArchitectureKindV1.X64, "1.0.0", InstallChannelV1.Stable, "website", null, null,
            null, "partial-idempotency"), null);

        await service.CompleteAsync(new DownloadCompleteRequestV1(
            downloadId, DownloadResultV1.Partial, Now.AddMinutes(1)));

        Assert.Equal(DownloadResult.Partial, Assert.Single(store.Downloads).Result);
    }

    [Fact]
    public async Task DiagnosticsRequireExplicitConsentAndOnlyApprovedCategories()
    {
        var store = new TestStore();
        var installation = Installation.Register(Guid.NewGuid(), SHA256.HashData("id"u8), PlatformKind.Windows,
            ArchitectureKind.X64, "1.0.0", "Windows 11", InstallChannel.Stable, "1", "eu-west", Now);
        var device = Device.Create(SHA256.HashData("id"u8), "123-***-***-012", "Device", new string('a', 64), Now);
        installation.AttachDevice(device.Id, SHA256.HashData("id"u8), Now);
        store.Installations.Add(installation);
        var retention = new RetentionOptions { Diagnostics = TimeSpan.FromDays(21) };
        var service = new DiagnosticsService(store, store, store, new CloudSecurityOptions(), retention, new ManualTimeProvider(Now));
        var principal = new DeviceAccessPrincipal(device.Id, installation.Id, Now.AddMinutes(15));
        var denied = new DiagnosticCreateRequestV1(installation.Id, false, ["sanitized-logs"], "1.0.0", "Windows 11", ArchitectureKindV1.X64, null, "network", 3);

        var exception = await Assert.ThrowsAsync<CloudServiceException>(() => service.CreateRequestAsync(principal, denied));
        Assert.Equal(CloudErrorCodes.DiagnosticConsentRequired, exception.Code);

        var created = await service.CreateRequestAsync(principal, denied with { ConsentGranted = true });
        Assert.NotEmpty(created.UploadToken);
        Assert.Equal(Now.AddDays(21), created.DiagnosticExpiresAtUtc);
        Assert.Single(store.Diagnostics);
        Assert.NotEqual(created.UploadToken, Convert.ToBase64String(store.Diagnostics[0].UploadTokenHash));
        var crossOwner = await Assert.ThrowsAsync<CloudServiceException>(() => service.GetStatusAsync(
            principal with { InstallationId = Guid.NewGuid() }, created.DiagnosticId));
        Assert.Equal(CloudErrorCodes.DiagnosticNotFound, crossOwner.Code);
    }

    [Fact]
    public async Task AuthenticatedClientSessionResolvesPeerWithoutAcceptingInternalPeerGuid()
    {
        const string callerPublicId = "111-111-111-111";
        const string peerPublicId = "222-222-222-222";
        var ids = new PublicDeviceIdService(RandomNumberGenerator.GetBytes(32));
        var caller = Device.Create(ids.ComputeLookupHash(callerPublicId), ids.Mask(callerPublicId), "Caller", new string('a', 64), Now);
        var peer = Device.Create(ids.ComputeLookupHash(peerPublicId), ids.Mask(peerPublicId), "Peer", new string('b', 64), Now);
        var store = new TestStore();
        store.Devices.AddRange([caller, peer]);
        var service = new SessionTelemetryService(store, store, ids, store, new CloudSecurityOptions(), new ManualTimeProvider(Now));
        var principal = new DeviceAccessPrincipal(caller.Id, Guid.NewGuid(), Now.AddMinutes(15));
        var sessionId = Guid.NewGuid();

        await service.RecordClientLifecycleAsync(principal, new ClientSessionLifecycleEventV1(Guid.NewGuid(), sessionId,
            SessionEventKindV1.Started, SessionParticipantRoleV1.Viewer, peerPublicId, PermissionModeV1.ViewOnly,
            ConnectionPathV1.Unknown, "eu-west", Now, Now, null, null, null, false, 0, "1.0.0"));

        var session = Assert.Single(store.Sessions);
        Assert.Equal(caller.Id, session.ViewerDeviceId);
        Assert.Equal(peer.Id, session.HostDeviceId);
        Assert.Equal("1.0.0", session.ClientVersionViewer);
        Assert.Null(session.ClientVersionHost);
    }

    [Fact]
    public async Task InstallationConfirmationRequiresProofBoundOwner()
    {
        var ids = new PublicDeviceIdService(RandomNumberGenerator.GetBytes(32));
        var store = new TestStore();
        var installationId = Guid.NewGuid();
        var hash = ids.ComputeLookupHash("111-111-111-111");
        var device = Device.Create(hash, ids.Mask("111-111-111-111"), "Device", new string('a', 64), Now);
        var installation = Installation.RegisterProofBound(installationId, device.Id, hash,
            PlatformKind.Windows, ArchitectureKind.X64, "1.0.0", "Windows 11", InstallChannel.Stable,
            "1", "eu-west", Now);
        store.Devices.Add(device);
        store.Installations.Add(installation);
        var service = new InstallationService(store, store, store, new CloudSecurityOptions(), new ManualTimeProvider(Now));
        var request = new InstallationRegistrationRequestV1(installationId, PlatformKindV1.Windows,
            ArchitectureKindV1.X64, "1.0.0", "Windows 11", InstallChannelV1.Stable, "1", "eu-west");
        await service.RegisterAsync(new DeviceAccessPrincipal(device.Id, installationId, Now.AddMinutes(10)), request);

        var exception = await Assert.ThrowsAsync<CloudServiceException>(() => service.RegisterAsync(
            new DeviceAccessPrincipal(Guid.NewGuid(), installationId, Now.AddMinutes(10)), request));

        Assert.Equal(CloudErrorCodes.InstallationIdentityConflict, exception.Code);
        Assert.Single(store.Installations);
    }

    [Fact]
    public async Task AuthenticatedUpdateEventResolvesReleaseAndIsIdempotent()
    {
        var ids = new PublicDeviceIdService(RandomNumberGenerator.GetBytes(32));
        var hash = ids.ComputeLookupHash("111-111-111-111");
        var device = Device.Create(hash, ids.Mask("111-111-111-111"), "Device", new string('a', 64), Now);
        var installation = Installation.Register(Guid.NewGuid(), hash, PlatformKind.Windows, ArchitectureKind.X64,
            "1.0.0", "Windows 11", InstallChannel.Stable, "1", "eu-west", Now);
        installation.AttachDevice(device.Id, hash, Now);
        var release = new AppRelease(Guid.NewGuid(), "1.1.0", InstallChannel.Stable, ArchitectureKind.X64, Now,
            "1.0.0", "1.0.0", 100, new string('b', 64), new Uri("https://download.peeronq.com/1.1.0/x64.msi"),
            1024, new string('c', 64));
        var store = new TestStore();
        store.Installations.Add(installation);
        store.Releases.Add(release);
        var service = new ReleaseTelemetryService(store, store, store);
        var principal = new DeviceAccessPrincipal(device.Id, installation.Id, Now.AddMinutes(15));
        var update = new ClientUpdateEventV1(Guid.NewGuid(), UpdateEventKindV1.Installed, "1.1.0",
            InstallChannelV1.Stable, ArchitectureKindV1.X64, null, Now.AddMinutes(1));

        await service.RecordClientUpdateEventAsync(principal, update);
        await service.RecordClientUpdateEventAsync(principal, update);

        Assert.Single(store.UpdateEvents);
        Assert.Equal(release.Id, store.UpdateEvents[0].ReleaseId);
    }

    [Fact]
    public async Task SessionHeartbeatIsIdempotentAndOwnershipBound()
    {
        var ids = new PublicDeviceIdService(RandomNumberGenerator.GetBytes(32));
        var store = new TestStore();
        var viewer = Device.Create(ids.ComputeLookupHash("111-111-111-111"), ids.Mask("111-111-111-111"), "Viewer", new string('a', 64), Now);
        var host = Device.Create(ids.ComputeLookupHash("222-222-222-222"), ids.Mask("222-222-222-222"), "Host", new string('b', 64), Now);
        store.Devices.AddRange([viewer, host]);
        var session = RemoteSession.Start(Guid.NewGuid(), viewer.Id, host.Id, PermissionMode.ViewOnly,
            "eu-west", "1.0.0", "1.0.0", Now.AddMinutes(-15));
        session.MarkConnected(ConnectionPath.InternetDirect, false, Now.AddMinutes(-14), Now.AddMinutes(-14));
        store.Sessions.Add(session);
        var service = new SessionTelemetryService(store, store, ids, store, new CloudSecurityOptions(), new ManualTimeProvider(Now));
        var eventId = Guid.NewGuid();
        var request = new ClientSessionHeartbeatV1(eventId, session.Id, Now);

        var first = await service.HeartbeatAsync(
            new DeviceAccessPrincipal(viewer.Id, Guid.NewGuid(), Now.AddMinutes(15)), request);
        var duplicate = await service.HeartbeatAsync(
            new DeviceAccessPrincipal(viewer.Id, Guid.NewGuid(), Now.AddMinutes(15)), request);

        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(Now, session.LastActivityAtUtc);
        Assert.Equal(1, store.Saves);
        var unauthorized = await Assert.ThrowsAsync<CloudServiceException>(() => service.HeartbeatAsync(
            new DeviceAccessPrincipal(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(15)),
            request with { EventId = Guid.NewGuid() }));
        Assert.Equal(CloudErrorCodes.SessionNotFound, unauthorized.Code);
        var staleClock = await Assert.ThrowsAsync<CloudServiceException>(() => service.HeartbeatAsync(
            new DeviceAccessPrincipal(viewer.Id, Guid.NewGuid(), Now.AddMinutes(15)),
            request with { EventId = Guid.NewGuid(), SentAtUtc = Now.AddMinutes(-6) }));
        Assert.Equal(CloudErrorCodes.InvalidRequest, staleClock.Code);
    }

    [Fact]
    public async Task ReconciliationUsesNegotiationAndConnectedActivityThresholds()
    {
        var ids = new PublicDeviceIdService(RandomNumberGenerator.GetBytes(32));
        var store = new TestStore();
        var viewer = Device.Create(ids.ComputeLookupHash("111-111-111-111"), ids.Mask("111-111-111-111"), "Viewer", new string('a', 64), Now);
        var host = Device.Create(ids.ComputeLookupHash("222-222-222-222"), ids.Mask("222-222-222-222"), "Host", new string('b', 64), Now);
        store.Devices.AddRange([viewer, host]);
        var negotiation = RemoteSession.Start(Guid.NewGuid(), viewer.Id, host.Id, PermissionMode.ViewOnly,
            "eu-west", "1.0.0", "1.0.0", Now.AddMinutes(-10));
        var missedHeartbeat = RemoteSession.Start(Guid.NewGuid(), viewer.Id, host.Id, PermissionMode.ViewOnly,
            "eu-west", "1.0.0", "1.0.0", Now.AddMinutes(-15));
        missedHeartbeat.MarkConnected(ConnectionPath.InternetDirect, false, Now.AddMinutes(-14), Now.AddMinutes(-10));
        var live = RemoteSession.Start(Guid.NewGuid(), viewer.Id, host.Id, PermissionMode.ViewOnly,
            "eu-west", "1.0.0", "1.0.0", Now.AddMinutes(-15));
        live.MarkConnected(ConnectionPath.InternetDirect, false, Now.AddMinutes(-14), Now.AddMinutes(-14));
        live.RecordActivity(viewer.Id, Guid.NewGuid(), Now);
        store.Sessions.AddRange([negotiation, missedHeartbeat, live]);
        var service = new SessionTelemetryService(store, store, ids, store, new CloudSecurityOptions(), new ManualTimeProvider(Now));

        var count = await service.ReconcileStaleAsync(Now.AddMinutes(-5), Now.AddMinutes(-3), 100);

        Assert.Equal(2, count);
        Assert.Equal(SessionLifecycle.Stale, negotiation.Lifecycle);
        Assert.Equal(SessionLifecycle.Stale, missedHeartbeat.Lifecycle);
        Assert.Equal(SessionLifecycle.Connected, live.Lifecycle);
    }

    [Fact]
    public async Task LegitimateEndCorrectsAReconciledSessionAfterLastHeartbeat()
    {
        const string peerPublicId = "222-222-222-222";
        var time = new ManualTimeProvider(Now);
        var ids = new PublicDeviceIdService(RandomNumberGenerator.GetBytes(32));
        var viewer = Device.Create(ids.ComputeLookupHash("111-111-111-111"), ids.Mask("111-111-111-111"), "Viewer", new string('a', 64), Now);
        var host = Device.Create(ids.ComputeLookupHash(peerPublicId), ids.Mask(peerPublicId), "Host", new string('b', 64), Now);
        var store = new TestStore();
        store.Devices.AddRange([viewer, host]);
        var session = RemoteSession.Start(Guid.NewGuid(), viewer.Id, host.Id, PermissionMode.ViewOnly,
            "eu-west", "1.0.0", "1.0.0", Now.AddMinutes(-15));
        session.MarkConnected(ConnectionPath.InternetDirect, false, Now.AddMinutes(-14), Now);
        store.Sessions.Add(session);
        var service = new SessionTelemetryService(store, store, ids, store, new CloudSecurityOptions(), time);
        time.UtcNow = Now.AddMinutes(4);
        await service.ReconcileStaleAsync(time.UtcNow.AddMinutes(-5), time.UtcNow.AddMinutes(-3), 100);
        Assert.Equal(SessionLifecycle.Stale, session.Lifecycle);

        await service.RecordClientLifecycleAsync(
            new DeviceAccessPrincipal(viewer.Id, Guid.NewGuid(), time.UtcNow.AddMinutes(15)),
            new ClientSessionLifecycleEventV1(Guid.NewGuid(), session.Id, SessionEventKindV1.Ended,
                SessionParticipantRoleV1.Viewer, peerPublicId, PermissionModeV1.ViewOnly,
                ConnectionPathV1.InternetDirect, "eu-west", session.StartedAtUtc, time.UtcNow,
                SessionEndReasonV1.Completed, null, null, false, 0, "1.0.0"));

        Assert.Equal(SessionLifecycle.Ended, session.Lifecycle);
        Assert.Equal(SessionEndReason.Completed, session.EndReason);
        Assert.Null(session.FailureCode);
    }
}
