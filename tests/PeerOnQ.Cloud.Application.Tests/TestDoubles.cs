using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;

namespace PeerOnQ.Cloud.Application.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class MemoryChallengeStore : IDeviceChallengeStore
{
    private readonly Dictionary<string, DeviceChallengeRecord> _records = new(StringComparer.Ordinal);
    public Task StoreAsync(DeviceChallengeRecord challenge, CancellationToken cancellationToken)
    {
        if (!_records.TryAdd(challenge.ChallengeId, challenge)) throw new InvalidOperationException();
        return Task.CompletedTask;
    }
    public Task<DeviceChallengeRecord?> ConsumeAsync(string challengeId, CancellationToken cancellationToken)
    {
        _records.Remove(challengeId, out var value);
        return Task.FromResult(value);
    }
}

internal sealed class MemoryTokenIssuer(ManualTimeProvider time) : IDeviceAccessTokenIssuer
{
    private readonly Dictionary<string, DeviceAccessPrincipal> _tokens = new(StringComparer.Ordinal);
    public Task<DeviceAccessToken> IssueAsync(Guid deviceId, Guid installationId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var principal = new DeviceAccessPrincipal(deviceId, installationId, time.GetUtcNow() + lifetime);
        _tokens[token] = principal;
        return Task.FromResult(new DeviceAccessToken(token, principal.ExpiresAtUtc));
    }
    public Task<DeviceAccessPrincipal?> ValidateAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(_tokens.GetValueOrDefault(token));
    public Task RevokeAsync(string token, CancellationToken cancellationToken) { _tokens.Remove(token); return Task.CompletedTask; }
}

internal sealed class MemorySignalingAttestationIssuer(ManualTimeProvider time) : ISignalingAttestationIssuer
{
    public IssuedSignalingAttestation Issue(SignalingAttestationIssueRequest request) =>
        new($"test-attestation:{request.PublicDeviceId}:{request.SpkiSha256}", time.GetUtcNow().AddMinutes(5));
}

internal sealed class TestStore : IDeviceRepository, IInstallationRepository, ISessionRepository,
    IDownloadRepository, IReleaseRepository, IDiagnosticRepository, ICloudUnitOfWork, IDeviceBootstrapStore
{
    public List<Device> Devices { get; } = [];
    public List<Installation> Installations { get; } = [];
    public List<RemoteSession> Sessions { get; } = [];
    public List<SessionFailure> Failures { get; } = [];
    public List<DownloadEvent> Downloads { get; } = [];
    public List<AppRelease> Releases { get; } = [];
    public List<UpdateEvent> UpdateEvents { get; } = [];
    public List<DiagnosticBundle> Diagnostics { get; } = [];
    public int Saves { get; private set; }
    public string? MinimumSupportedVersion { get; set; }
    public IPublicDeviceIdService? PublicDeviceIds { get; set; }

    public Task<Device?> FindByPublicIdHashAsync(byte[] hash, CancellationToken cancellationToken) =>
        Task.FromResult(Devices.SingleOrDefault(value => value.PublicDeviceIdHash.SequenceEqual(hash)));
    public Task<Device?> FindByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Devices.SingleOrDefault(value => value.Id == id));
    public Task<Device?> FindByIdentityFingerprintAsync(string fingerprint, CancellationToken cancellationToken) => Task.FromResult(Devices.SingleOrDefault(value => value.IdentityFingerprint == fingerprint));
    public void Add(Device device) => Devices.Add(device);
    Task<Installation?> IInstallationRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Installations.SingleOrDefault(value => value.Id == id));
    void IInstallationRepository.Add(Installation installation) => Installations.Add(installation);
    Task<RemoteSession?> ISessionRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Sessions.SingleOrDefault(value => value.Id == id));
    void ISessionRepository.Add(RemoteSession session) => Sessions.Add(session);
    void ISessionRepository.AddFailure(SessionFailure failure) => Failures.Add(failure);
    Task<IReadOnlyList<RemoteSession>> ISessionRepository.FindStaleActiveAsync(
        DateTimeOffset negotiationBeforeUtc,
        DateTimeOffset connectedBeforeUtc,
        int maximumCount,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RemoteSession>>(Sessions
            .Where(value => value.EndedAtUtc is null &&
                ((value.Lifecycle == SessionLifecycle.Starting && value.LastActivityAtUtc < negotiationBeforeUtc) ||
                 (value.Lifecycle == SessionLifecycle.Connected && value.LastActivityAtUtc < connectedBeforeUtc)))
            .OrderBy(value => value.LastActivityAtUtc)
            .Take(maximumCount)
            .ToList());
    Task<DownloadEvent?> IDownloadRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Downloads.SingleOrDefault(value => value.Id == id));
    Task<DownloadEvent?> IDownloadRepository.FindByIdempotencyHashAsync(byte[] hash, CancellationToken cancellationToken) => Task.FromResult(Downloads.SingleOrDefault(value => value.IdempotencyKeyHash.SequenceEqual(hash)));
    void IDownloadRepository.Add(DownloadEvent download) => Downloads.Add(download);
    Task<AppRelease?> IReleaseRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Releases.SingleOrDefault(value => value.Id == id));
    Task<AppRelease?> IReleaseRepository.FindByVersionAsync(string version, InstallChannel channel, ArchitectureKind architecture, CancellationToken cancellationToken) =>
        Task.FromResult(Releases.SingleOrDefault(value => value.Version == version && value.Channel == channel && value.Architecture == architecture));
    Task<string?> IReleaseRepository.FindMinimumSupportedVersionAsync(InstallChannel channel, ArchitectureKind architecture, CancellationToken cancellationToken) =>
        Task.FromResult(MinimumSupportedVersion ?? Releases.Where(value => value.Channel == channel && value.Architecture == architecture && value.IsActive).OrderByDescending(value => value.PublishedAtUtc).Select(value => value.MinimumSupportedVersion).FirstOrDefault());
    Task<bool> IReleaseRepository.UpdateEventExistsAsync(Guid eventId, CancellationToken cancellationToken) => Task.FromResult(UpdateEvents.Any(value => value.Id == eventId));
    void IReleaseRepository.AddUpdateEvent(UpdateEvent updateEvent) => UpdateEvents.Add(updateEvent);
    Task<DiagnosticBundle?> IDiagnosticRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Diagnostics.SingleOrDefault(value => value.Id == id));
    void IDiagnosticRepository.Add(DiagnosticBundle bundle) => Diagnostics.Add(bundle);
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) { Saves++; return Task.FromResult(1); }

    public Task<DeviceBootstrapResult> CompleteAsync(DeviceBootstrapRequest request, CancellationToken cancellationToken)
    {
        var publicIds = PublicDeviceIds ?? throw new InvalidOperationException("Public ID service is required.");
        var existingInstallation = Installations.SingleOrDefault(value => value.Id == request.InstallationId);
        if (existingInstallation?.DeviceId is { } boundDeviceId &&
            Devices.Single(value => value.Id == boundDeviceId).IdentityFingerprint != request.IdentityFingerprint)
        {
            throw new CloudServiceException(CloudErrorCodes.InstallationIdentityConflict,
                "Installation proof conflict.");
        }
        var device = Devices.SingleOrDefault(value => value.IdentityFingerprint == request.IdentityFingerprint);
        var deviceCreated = device is null;
        ServerAssignedPublicDeviceId alias;
        if (device is null)
        {
            alias = Enumerable.Range(0, 4096)
                .Select(counter => publicIds.DeriveFromFingerprint(request.IdentityFingerprint, counter))
                .First(candidate => Devices.All(value => !value.PublicDeviceIdHash.SequenceEqual(candidate.LookupHash)));
            device = Device.Create(alias.LookupHash, alias.MaskedValue, request.DisplayName,
                request.IdentityFingerprint, alias.CollisionCounter, alias.KeyVersion, request.AuthenticatedAtUtc);
            Devices.Add(device);
        }
        else
        {
            if (device.IsRevoked)
                throw new CloudServiceException(CloudErrorCodes.DeviceRevoked, "The device identity is revoked.");
            if (device.PublicDeviceIdCollisionCounter < 0)
            {
                alias = Enumerable.Range(0, 4096)
                    .Select(counter => publicIds.DeriveFromFingerprint(request.IdentityFingerprint, counter))
                    .First(candidate => Devices.All(value => value.Id == device.Id ||
                        !value.PublicDeviceIdHash.SequenceEqual(candidate.LookupHash)));
                device.AssignServerAlias(alias.LookupHash, alias.MaskedValue, alias.CollisionCounter,
                    alias.KeyVersion, request.AuthenticatedAtUtc);
            }
            else
            {
                alias = publicIds.DeriveFromFingerprint(request.IdentityFingerprint,
                    device.PublicDeviceIdCollisionCounter, device.PublicDeviceIdKeyVersion);
            }
            device.Touch(request.DisplayName, request.AuthenticatedAtUtc);
        }

        var installation = existingInstallation;
        var installationCreated = installation is null;
        if (installation is null)
        {
            installation = Installation.RegisterProofBound(request.InstallationId, device.Id, alias.LookupHash,
                request.Platform, request.Architecture, request.AppVersion, request.OsVersion,
                request.InstallChannel, request.ProtocolVersion, request.Region, request.AuthenticatedAtUtc);
            Installations.Add(installation);
        }
        else
        {
            try
            {
                installation.ConfirmProofBinding(device.Id, alias.LookupHash, request.Platform,
                    request.Architecture, request.AppVersion, request.OsVersion, request.InstallChannel,
                    request.ProtocolVersion, request.Region, request.AuthenticatedAtUtc);
            }
            catch (InvalidOperationException exception)
            {
                throw new CloudServiceException(CloudErrorCodes.InstallationIdentityConflict,
                    "Installation proof conflict.", true, exception);
            }
        }

        return Task.FromResult(new DeviceBootstrapResult(device.Id, installation.Id, alias.Value,
            deviceCreated, installationCreated));
    }
}

internal sealed class EmptyArtifactRepository : IDownloadArtifactRepository
{
    public Task<DownloadArtifact?> ResolveLatestAsync(PlatformKind platform, ArchitectureKind architecture, InstallChannel channel, CancellationToken cancellationToken) => Task.FromResult<DownloadArtifact?>(null);
    public Task<DownloadArtifact?> ResolveVersionAsync(PlatformKind platform, ArchitectureKind architecture, string version, CancellationToken cancellationToken) => Task.FromResult<DownloadArtifact?>(null);
}
