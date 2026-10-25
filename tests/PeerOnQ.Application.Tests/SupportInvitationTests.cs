using System.Text.Json;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class SupportInvitationTests
{
    private static readonly PeerOnQId Host = PeerOnQId.Parse("LNK-111-222-333-444");
    private static readonly PeerOnQId Technician = PeerOnQId.Parse("LNK-555-666-777-888");
    private static readonly string Fingerprint = new('a', 64);

    [Fact]
    public async Task Invitation_is_hash_stored_permission_bound_and_single_use_after_approval()
    {
        var store = new MemoryInvitationStore();
        var audit = new MemorySecurityAudit();
        var service = new SupportInvitationService(store, audit);
        var issued = await service.IssueAsync(Host, new SupportInvitationRequest
        {
            Mode = SessionMode.FullControl,
            Permissions = SessionPermission.ViewScreen | SessionPermission.ControlInput,
            MaximumUses = 1,
            SupportNote = "Investigate the display driver.",
        });

        Assert.StartsWith("peeronq://support?", issued.OpenUri, StringComparison.Ordinal);
        Assert.True(SupportInvitationLink.TryParse(issued.OpenUri, out var link));
        Assert.Equal(Host, link.TargetDevice);
        Assert.Equal(issued.Token, link.Token);
        Assert.Equal(issued.Mode, link.Mode);
        Assert.Equal(issued.Permissions, link.Permissions);
        Assert.DoesNotContain(issued.Token, JsonSerializer.Serialize(store.Records), StringComparison.Ordinal);
        Assert.Equal(32, Assert.Single(store.Records).TokenHash.Length);

        var mismatch = await service.ValidateAsync(
            issued.Token,
            null,
            Technician,
            Fingerprint,
            SessionMode.ViewOnly,
            SessionPermission.ViewScreen);
        Assert.Equal(SupportInvitationValidationCode.PermissionMismatch, mismatch.Code);

        var valid = await service.ValidateAsync(
            issued.Token,
            null,
            Technician,
            Fingerprint,
            SessionMode.FullControl,
            SessionPermission.ViewScreen | SessionPermission.ControlInput);
        Assert.True(valid.IsValid);
        Assert.Equal("Investigate the display driver.", valid.Authorization!.SupportNote);
        Assert.Equal(0, Assert.Single((await service.ListAsync())).UseCount);

        Assert.True(await service.TryConsumeApprovedAsync(valid.Authorization));
        Assert.Equal(1, Assert.Single((await service.ListAsync())).UseCount);

        var replay = await service.ValidateAsync(
            issued.Token,
            null,
            Technician,
            Fingerprint,
            SessionMode.FullControl,
            SessionPermission.ViewScreen | SessionPermission.ControlInput);
        Assert.Equal(SupportInvitationValidationCode.Exhausted, replay.Code);
        Assert.Contains(audit.Events, item => item.EventType == SecurityAuditEventType.SupportInvitationUsed);
    }

    [Fact]
    public async Task Password_restrictions_expiry_and_revocation_fail_closed()
    {
        var time = new ManualClock(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var service = new SupportInvitationService(new MemoryInvitationStore(), timeProvider: time);
        var issued = await service.IssueAsync(Host, new SupportInvitationRequest
        {
            Mode = SessionMode.ViewOnly,
            Permissions = SessionPermission.ViewScreen,
            Lifetime = TimeSpan.FromMinutes(5),
            Password = "Strong-Support-9!", // secret-scan: allow-test-vector
            AllowedRequesterDevice = Technician,
            AllowedTechnicianFingerprint = Fingerprint,
        });

        var missingPassword = await ValidateAsync(service, issued, Technician, Fingerprint, null);
        Assert.Equal(SupportInvitationValidationCode.PasswordRequired, missingPassword.Code);
        var wrongPassword = await ValidateAsync(service, issued, Technician, Fingerprint, "Wrong-Support-9!");
        Assert.Equal(SupportInvitationValidationCode.PasswordInvalid, wrongPassword.Code);
        var wrongDevice = await ValidateAsync(service, issued, PeerOnQId.Parse("LNK-999-000-111-222"), Fingerprint, "Strong-Support-9!");
        Assert.Equal(SupportInvitationValidationCode.RequesterRestricted, wrongDevice.Code);
        var wrongTechnician = await ValidateAsync(service, issued, Technician, new string('b', 64), "Strong-Support-9!");
        Assert.Equal(SupportInvitationValidationCode.TechnicianRestricted, wrongTechnician.Code);
        var malformedTechnician = await ValidateAsync(service, issued, Technician, "not-a-fingerprint", "Strong-Support-9!");
        Assert.Equal(SupportInvitationValidationCode.TechnicianRestricted, malformedTechnician.Code);
        Assert.True((await ValidateAsync(service, issued, Technician, Fingerprint, "Strong-Support-9!")).IsValid);

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(
            SupportInvitationValidationCode.Expired,
            (await ValidateAsync(service, issued, Technician, Fingerprint, "Strong-Support-9!")).Code);

        var revocable = await service.IssueAsync(Host, new SupportInvitationRequest
        {
            Mode = SessionMode.ViewOnly,
            Permissions = SessionPermission.ViewScreen,
        });
        Assert.True(await service.RevokeAsync(revocable.InvitationId));
        Assert.Equal(
            SupportInvitationValidationCode.Revoked,
            (await ValidateAsync(service, revocable, Technician, Fingerprint, null)).Code);
    }

    [Fact]
    public async Task Guessing_attempts_are_bounded_per_requester()
    {
        var service = new SupportInvitationService(new MemoryInvitationStore());
        SupportInvitationValidationResult? result = null;
        for (var index = 0; index <= 10; index++)
        {
            result = await service.ValidateAsync(
                "not-a-valid-token",
                null,
                Technician,
                Fingerprint,
                SessionMode.ViewOnly,
                SessionPermission.ViewScreen);
        }

        Assert.Equal(SupportInvitationValidationCode.RateLimited, result!.Code);
    }

    [Fact]
    public async Task Protected_store_round_trips_only_the_token_hash()
    {
        var secrets = new MemoryDeviceSecretStore();
        var service = new SupportInvitationService(new ProtectedSupportInvitationStore(secrets));
        var issued = await service.IssueAsync(Host, new SupportInvitationRequest
        {
            Mode = SessionMode.ViewOnly,
            Permissions = SessionPermission.ViewScreen,
        });

        Assert.NotNull(secrets.StoredBytes);
        Assert.DoesNotContain(issued.Token, System.Text.Encoding.UTF8.GetString(secrets.StoredBytes!), StringComparison.Ordinal);

        var reloaded = new SupportInvitationService(new ProtectedSupportInvitationStore(secrets));
        Assert.True((await ValidateAsync(reloaded, issued, Technician, Fingerprint, null)).IsValid);
    }

    private static Task<SupportInvitationValidationResult> ValidateAsync(
        SupportInvitationService service,
        IssuedSupportInvitation issued,
        PeerOnQId requester,
        string fingerprint,
        string? password) =>
        service.ValidateAsync(
            issued.Token,
            password,
            requester,
            fingerprint,
            issued.Mode,
            issued.Permissions);

    private sealed class MemoryInvitationStore : ISupportInvitationStore
    {
        public IReadOnlyList<SupportInvitationRecord> Records { get; private set; } = [];

        public Task<IReadOnlyList<SupportInvitationRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Records);

        public Task SaveAsync(
            IReadOnlyList<SupportInvitationRecord> invitations,
            CancellationToken cancellationToken = default)
        {
            Records = invitations.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySecurityAudit : ISecurityAuditLog
    {
        public List<SecurityAuditEvent> Events { get; } = [];
        public Task AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SecurityAuditEvent>> ReadRecentAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SecurityAuditEvent>>(Events.TakeLast(limit).ToArray());
        public Task<AuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditIntegrityResult(true, Events.Count, null));
        public Task ExportSanitizedJsonLinesAsync(string destinationFile, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ApplyRetentionAsync(TimeSpan retention, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ClearAsync(bool confirmed, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryDeviceSecretStore : IDeviceSecretStore
    {
        public byte[]? StoredBytes { get; private set; }

        public Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(StoredBytes?.ToArray());

        public Task SetAsync(string name, byte[] secret, CancellationToken cancellationToken = default)
        {
            StoredBytes = secret.ToArray();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
        {
            StoredBytes = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
