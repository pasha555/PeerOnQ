using Microsoft.EntityFrameworkCore;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;

namespace PeerOnQ.Cloud.Infrastructure.Persistence;

public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : DbContext(options), ICloudUnitOfWork
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<DevicePresenceHistory> DevicePresenceHistory => Set<DevicePresenceHistory>();
    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();
    public DbSet<SessionFailure> SessionFailures => Set<SessionFailure>();
    public DbSet<DownloadEvent> DownloadEvents => Set<DownloadEvent>();
    public DbSet<AppRelease> AppReleases => Set<AppRelease>();
    public DbSet<UpdateEvent> UpdateEvents => Set<UpdateEvent>();
    public DbSet<DiagnosticBundle> DiagnosticBundles => Set<DiagnosticBundle>();
    public DbSet<DiagnosticAccessEvent> DiagnosticAccessEvents => Set<DiagnosticAccessEvent>();
    public DbSet<CustomerAccount> CustomerAccounts => Set<CustomerAccount>();
    public DbSet<CustomerSession> CustomerSessions => Set<CustomerSession>();
    public DbSet<CustomerAccountToken> CustomerAccountTokens => Set<CustomerAccountToken>();
    public DbSet<CustomerRecoveryCode> CustomerRecoveryCodes => Set<CustomerRecoveryCode>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
    public DbSet<OrganizationInvitation> OrganizationInvitations => Set<OrganizationInvitation>();
    public DbSet<OrganizationPolicy> OrganizationPolicies => Set<OrganizationPolicy>();
    public DbSet<CustomerTrustedDevice> CustomerTrustedDevices => Set<CustomerTrustedDevice>();
    public DbSet<CustomerSecurityEvent> CustomerSecurityEvents => Set<CustomerSecurityEvent>();
    public DbSet<AccountDataRequest> AccountDataRequests => Set<AccountDataRequest>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AdminRole> AdminRoles => Set<AdminRole>();
    public DbSet<AdminUserRole> AdminUserRoles => Set<AdminUserRole>();
    public DbSet<AdminRecoveryCode> AdminRecoveryCodes => Set<AdminRecoveryCode>();
    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<InfrastructureRegion> InfrastructureRegions => Set<InfrastructureRegion>();
    public DbSet<ServiceHealthSnapshot> ServiceHealthSnapshots => Set<ServiceHealthSnapshot>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<RetentionBatchEvidence> RetentionBatchEvidenceEntries => Set<RetentionBatchEvidence>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAppendOnlyRecords();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceAppendOnlyRecords();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureDevice(modelBuilder);
        ConfigureInstallation(modelBuilder);
        ConfigureOperationalTelemetry(modelBuilder);
        ConfigureReleaseAndDiagnostics(modelBuilder);
        ConfigureCustomerIdentity(modelBuilder);
        ConfigureAdministration(modelBuilder);
        ConfigureInfrastructure(modelBuilder);
    }

    private static void ConfigureCustomerIdentity(ModelBuilder modelBuilder)
    {
        var account = modelBuilder.Entity<CustomerAccount>();
        account.ToTable("CustomerAccounts");
        account.HasKey(value => value.Id);
        account.Property(value => value.Id).ValueGeneratedNever();
        account.Property(value => value.Email).HasMaxLength(320).IsRequired();
        account.Property(value => value.DisplayName).HasMaxLength(128).IsRequired();
        account.Property(value => value.PasswordHash).HasMaxLength(2048).IsRequired();
        account.Property(value => value.Status).HasConversion<string>().HasMaxLength(32);
        account.Property(value => value.MfaSecretCiphertext).HasMaxLength(4096);
        account.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        account.HasIndex(value => value.Email).IsUnique();
        account.HasIndex(value => value.Status);

        var session = modelBuilder.Entity<CustomerSession>();
        session.ToTable("CustomerSessions");
        session.HasKey(value => value.Id);
        session.Property(value => value.Id).ValueGeneratedNever();
        session.Property(value => value.RefreshTokenHash).HasMaxLength(32).IsRequired();
        session.Property(value => value.UserAgentSummary).HasMaxLength(256).IsRequired();
        session.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        session.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);
        session.HasOne<CustomerSession>().WithMany().HasForeignKey(value => value.ReplacedBySessionId).OnDelete(DeleteBehavior.Restrict);
        session.HasIndex(value => value.RefreshTokenHash).IsUnique();
        session.HasIndex(value => new { value.AccountId, value.ExpiresAtUtc });
        session.HasIndex(value => value.FamilyId);

        var token = modelBuilder.Entity<CustomerAccountToken>();
        token.ToTable("CustomerAccountTokens");
        token.HasKey(value => value.Id);
        token.Property(value => value.Id).ValueGeneratedNever();
        token.Property(value => value.Purpose).HasConversion<string>().HasMaxLength(32);
        token.Property(value => value.TokenHash).HasMaxLength(32).IsRequired();
        token.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);
        token.HasIndex(value => value.TokenHash).IsUnique();
        token.HasIndex(value => new { value.AccountId, value.Purpose, value.ExpiresAtUtc });

        var recovery = modelBuilder.Entity<CustomerRecoveryCode>();
        recovery.ToTable("CustomerRecoveryCodes");
        recovery.HasKey(value => value.Id);
        recovery.Property(value => value.Id).ValueGeneratedNever();
        recovery.Property(value => value.CodeHash).HasMaxLength(32).IsRequired();
        recovery.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);
        recovery.HasIndex(value => new { value.AccountId, value.CodeHash }).IsUnique();

        var organization = modelBuilder.Entity<Organization>();
        organization.ToTable("Organizations");
        organization.HasKey(value => value.Id);
        organization.Property(value => value.Id).ValueGeneratedNever();
        organization.Property(value => value.Name).HasMaxLength(128).IsRequired();
        organization.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        organization.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.OwnerAccountId).OnDelete(DeleteBehavior.Restrict);

        var membership = modelBuilder.Entity<OrganizationMembership>();
        membership.ToTable("OrganizationMemberships");
        membership.HasKey(value => new { value.OrganizationId, value.AccountId });
        membership.Property(value => value.Role).HasConversion<string>().HasMaxLength(32);
        membership.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        membership.HasOne<Organization>().WithMany().HasForeignKey(value => value.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        membership.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);
        membership.HasIndex(value => new { value.AccountId, value.RevokedAtUtc });

        var team = modelBuilder.Entity<Team>();
        team.ToTable("Teams");
        team.HasKey(value => value.Id);
        team.Property(value => value.Id).ValueGeneratedNever();
        team.Property(value => value.Name).HasMaxLength(128).IsRequired();
        team.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        team.HasOne<Organization>().WithMany().HasForeignKey(value => value.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        team.HasIndex(value => new { value.OrganizationId, value.Name }).IsUnique();

        var teamMembership = modelBuilder.Entity<TeamMembership>();
        teamMembership.ToTable("TeamMemberships");
        teamMembership.HasKey(value => new { value.TeamId, value.AccountId });
        teamMembership.HasOne<Team>().WithMany().HasForeignKey(value => value.TeamId).OnDelete(DeleteBehavior.Cascade);
        teamMembership.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);

        var invitation = modelBuilder.Entity<OrganizationInvitation>();
        invitation.ToTable("OrganizationInvitations");
        invitation.HasKey(value => value.Id);
        invitation.Property(value => value.Id).ValueGeneratedNever();
        invitation.Property(value => value.Email).HasMaxLength(320).IsRequired();
        invitation.Property(value => value.Role).HasConversion<string>().HasMaxLength(32);
        invitation.Property(value => value.TokenHash).HasMaxLength(32).IsRequired();
        invitation.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        invitation.HasOne<Organization>().WithMany().HasForeignKey(value => value.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        invitation.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.InvitedByAccountId).OnDelete(DeleteBehavior.Restrict);
        invitation.HasIndex(value => value.TokenHash).IsUnique();
        invitation.HasIndex(value => new { value.OrganizationId, value.Email, value.ExpiresAtUtc });

        var policy = modelBuilder.Entity<OrganizationPolicy>();
        policy.ToTable("OrganizationPolicies");
        policy.HasKey(value => value.OrganizationId);
        policy.Property(value => value.ApprovedRelayRegionsCsv).HasMaxLength(512).IsRequired();
        policy.Property(value => value.MinimumClientVersion).HasMaxLength(64).IsRequired();
        policy.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        policy.HasOne<Organization>().WithOne().HasForeignKey<OrganizationPolicy>(value => value.OrganizationId).OnDelete(DeleteBehavior.Cascade);

        var trusted = modelBuilder.Entity<CustomerTrustedDevice>();
        trusted.ToTable("CustomerTrustedDevices");
        trusted.HasKey(value => value.Id);
        trusted.Property(value => value.Id).ValueGeneratedNever();
        trusted.Property(value => value.DeviceKeyHash).HasMaxLength(32).IsRequired();
        trusted.Property(value => value.Name).HasMaxLength(128).IsRequired();
        trusted.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);
        trusted.HasIndex(value => value.DeviceKeyHash).IsUnique();
        trusted.HasIndex(value => new { value.AccountId, value.ExpiresAtUtc });

        var securityEvent = modelBuilder.Entity<CustomerSecurityEvent>();
        securityEvent.ToTable("CustomerSecurityEvents");
        securityEvent.HasKey(value => value.Id);
        securityEvent.Property(value => value.Id).ValueGeneratedNever();
        securityEvent.Property(value => value.Action).HasMaxLength(128).IsRequired();
        securityEvent.Property(value => value.Result).HasConversion<string>().HasMaxLength(32);
        securityEvent.Property(value => value.CorrelationId).HasMaxLength(128).IsRequired();
        securityEvent.Property(value => value.SafeMetadata).HasMaxLength(512);
        securityEvent.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Restrict);
        securityEvent.HasOne<Organization>().WithMany().HasForeignKey(value => value.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        securityEvent.HasIndex(value => new { value.AccountId, value.TimestampUtc });
        securityEvent.HasIndex(value => new { value.OrganizationId, value.TimestampUtc });

        var dataRequest = modelBuilder.Entity<AccountDataRequest>();
        dataRequest.ToTable("AccountDataRequests");
        dataRequest.HasKey(value => value.Id);
        dataRequest.Property(value => value.Id).ValueGeneratedNever();
        dataRequest.Property(value => value.Kind).HasConversion<string>().HasMaxLength(32);
        dataRequest.Property(value => value.Status).HasConversion<string>().HasMaxLength(32);
        dataRequest.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Restrict);
        dataRequest.HasIndex(value => new { value.AccountId, value.RequestedAtUtc });

        var device = modelBuilder.Entity<Device>();
        device.HasOne<CustomerAccount>().WithMany().HasForeignKey(value => value.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        device.HasOne<Organization>().WithMany().HasForeignKey(value => value.OwnerOrganizationId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDevice(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Device>();
        entity.ToTable("Devices");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).ValueGeneratedNever();
        entity.Property(value => value.PublicDeviceIdHash).HasMaxLength(32).IsRequired();
        entity.Property(value => value.MaskedPublicDeviceId).HasMaxLength(32).IsRequired();
        entity.Property(value => value.DisplayName).HasMaxLength(128).IsRequired();
        entity.Property(value => value.IdentityFingerprint).HasMaxLength(64).IsRequired();
        entity.Property(value => value.PublicDeviceIdCollisionCounter).IsRequired();
        entity.Property(value => value.PublicDeviceIdKeyVersion).IsRequired();
        entity.Property(value => value.RevocationReason).HasMaxLength(256);
        entity.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        entity.HasIndex(value => value.PublicDeviceIdHash).IsUnique();
        entity.HasIndex(value => value.IdentityFingerprint).IsUnique();
        entity.HasIndex(value => value.LastSeenAtUtc);
        entity.HasIndex(value => value.OwnerUserId);
        entity.HasIndex(value => value.OwnerOrganizationId);
    }

    private static void ConfigureInstallation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Installation>();
        entity.ToTable("Installations");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).ValueGeneratedNever();
        entity.Property(value => value.ClaimedPublicDeviceIdHash).HasMaxLength(32).IsRequired();
        entity.Property(value => value.Platform).HasConversion<string>().HasMaxLength(32);
        entity.Property(value => value.Architecture).HasConversion<string>().HasMaxLength(32);
        entity.Property(value => value.InstallChannel).HasConversion<string>().HasMaxLength(32);
        entity.Property(value => value.AppVersion).HasMaxLength(64).IsRequired();
        entity.Property(value => value.OsVersion).HasMaxLength(128).IsRequired();
        entity.Property(value => value.ProtocolVersion).HasMaxLength(32).IsRequired();
        entity.Property(value => value.BlockReason).HasMaxLength(256);
        entity.Property(value => value.CurrentRegion).HasMaxLength(64).IsRequired();
        entity.Property(value => value.ProofBindingVersion).IsRequired();
        entity.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        entity.HasOne<Device>().WithMany().HasForeignKey(value => value.DeviceId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(value => value.DeviceId);
        entity.HasIndex(value => value.LastOnlineAtUtc);
        entity.HasIndex(value => value.AppVersion);
        entity.HasIndex(value => new { value.CurrentRegion, value.IsBlocked });
        entity.HasIndex(value => value.ClaimedPublicDeviceIdHash);
    }

    private static void ConfigureOperationalTelemetry(ModelBuilder modelBuilder)
    {
        var presence = modelBuilder.Entity<DevicePresenceHistory>();
        presence.ToTable("DevicePresenceHistory");
        presence.HasKey(value => value.Id);
        presence.Property(value => value.Id).ValueGeneratedNever();
        presence.Property(value => value.State).HasConversion<string>().HasMaxLength(32);
        presence.Property(value => value.Region).HasMaxLength(64).IsRequired();
        presence.HasOne<Installation>().WithMany().HasForeignKey(value => value.InstallationId).OnDelete(DeleteBehavior.Restrict);
        presence.HasIndex(value => new { value.InstallationId, value.IntervalStartedAtUtc });
        presence.HasIndex(value => value.IntervalEndedAtUtc);

        var session = modelBuilder.Entity<RemoteSession>();
        session.ToTable("RemoteSessions");
        session.HasKey(value => value.Id);
        session.Property(value => value.Id).ValueGeneratedNever();
        session.Property(value => value.PermissionMode).HasConversion<string>().HasMaxLength(32);
        session.Property(value => value.ConnectionPath).HasConversion<string>().HasMaxLength(32);
        session.Property(value => value.EndReason).HasConversion<string>().HasMaxLength(64);
        session.Property(value => value.Lifecycle).HasConversion<string>().HasMaxLength(32);
        session.Property(value => value.ServerRegion).HasMaxLength(64).IsRequired();
        session.Property(value => value.ClientVersionViewer).HasMaxLength(64);
        session.Property(value => value.ClientVersionHost).HasMaxLength(64);
        session.Property(value => value.FailureStage).HasMaxLength(64);
        session.Property(value => value.FailureCode).HasMaxLength(128);
        session.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        session.HasOne<Device>().WithMany().HasForeignKey(value => value.ViewerDeviceId).OnDelete(DeleteBehavior.Restrict);
        session.HasOne<Device>().WithMany().HasForeignKey(value => value.HostDeviceId).OnDelete(DeleteBehavior.Restrict);
        session.HasIndex(value => value.StartedAtUtc);
        session.HasIndex(value => value.ViewerDeviceId);
        session.HasIndex(value => value.HostDeviceId);
        session.HasIndex(value => value.EndedAtUtc).HasFilter("\"EndedAtUtc\" IS NULL");
        session.HasIndex(value => new { value.Lifecycle, value.LastActivityAtUtc })
            .HasFilter("\"EndedAtUtc\" IS NULL");
        session.HasIndex(value => new { value.ServerRegion, value.UsedTurn, value.StartedAtUtc });

        var failure = modelBuilder.Entity<SessionFailure>();
        failure.ToTable("SessionFailures");
        failure.HasKey(value => value.Id);
        failure.Property(value => value.Id).ValueGeneratedNever();
        failure.Property(value => value.Stage).HasMaxLength(64).IsRequired();
        failure.Property(value => value.Code).HasMaxLength(128).IsRequired();
        failure.HasOne<RemoteSession>().WithMany().HasForeignKey(value => value.SessionId).OnDelete(DeleteBehavior.Cascade);
        failure.HasIndex(value => new { value.OccurredAtUtc, value.Code });

        var download = modelBuilder.Entity<DownloadEvent>();
        download.ToTable("DownloadEvents");
        download.HasKey(value => value.Id);
        download.Property(value => value.Id).ValueGeneratedNever();
        download.Property(value => value.IdempotencyKeyHash).HasMaxLength(32).IsRequired();
        download.Property(value => value.UniquenessKeyHash).HasMaxLength(32);
        download.Property(value => value.Platform).HasConversion<string>().HasMaxLength(32);
        download.Property(value => value.Architecture).HasConversion<string>().HasMaxLength(32);
        download.Property(value => value.Channel).HasConversion<string>().HasMaxLength(32);
        download.Property(value => value.Result).HasConversion<string>().HasMaxLength(32);
        download.Property(value => value.Version).HasMaxLength(64).IsRequired();
        download.Property(value => value.Source).HasMaxLength(64).IsRequired();
        download.Property(value => value.Campaign).HasMaxLength(128);
        download.Property(value => value.CountryCode).HasMaxLength(2);
        download.Property(value => value.UserAgentFamily).HasMaxLength(64);
        download.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        download.HasIndex(value => value.IdempotencyKeyHash).IsUnique();
        download.HasIndex(value => new { value.StartedAtUtc, value.Version });
        download.HasIndex(value => new { value.UniquenessKeyHash, value.StartedAtUtc });
        download.HasIndex(value => value.CompletedAtUtc);
    }

    private static void ConfigureReleaseAndDiagnostics(ModelBuilder modelBuilder)
    {
        var release = modelBuilder.Entity<AppRelease>();
        release.ToTable("AppReleases");
        release.HasKey(value => value.Id);
        release.Property(value => value.Id).ValueGeneratedNever();
        release.Property(value => value.Version).HasMaxLength(64).IsRequired();
        release.Property(value => value.Channel).HasConversion<string>().HasMaxLength(32);
        release.Property(value => value.Architecture).HasConversion<string>().HasMaxLength(32);
        release.Property(value => value.MinimumSupportedVersion).HasMaxLength(64).IsRequired();
        release.Property(value => value.SecurityFloorVersion).HasMaxLength(64).IsRequired();
        release.Property(value => value.SignedManifestDigest).HasMaxLength(64).IsRequired();
        release.Property(value => value.ArtifactUri).HasMaxLength(2048).IsRequired();
        release.Property(value => value.ArtifactSha256).HasMaxLength(64).IsRequired();
        release.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        release.HasIndex(value => new { value.Version, value.Channel, value.Architecture }).IsUnique();
        release.HasIndex(value => new { value.Channel, value.Architecture, value.IsActive });

        var updateEvent = modelBuilder.Entity<UpdateEvent>();
        updateEvent.ToTable("UpdateEvents");
        updateEvent.HasKey(value => value.Id);
        updateEvent.Property(value => value.Id).ValueGeneratedNever();
        updateEvent.Property(value => value.Kind).HasConversion<string>().HasMaxLength(32);
        updateEvent.Property(value => value.FailureCode).HasMaxLength(128);
        updateEvent.HasOne<Installation>().WithMany().HasForeignKey(value => value.InstallationId).OnDelete(DeleteBehavior.Restrict);
        updateEvent.HasOne<AppRelease>().WithMany().HasForeignKey(value => value.ReleaseId).OnDelete(DeleteBehavior.Restrict);
        updateEvent.HasIndex(value => new { value.InstallationId, value.OccurredAtUtc });
        updateEvent.HasIndex(value => new { value.ReleaseId, value.Kind, value.OccurredAtUtc });

        var diagnostic = modelBuilder.Entity<DiagnosticBundle>();
        diagnostic.ToTable("DiagnosticBundles");
        diagnostic.HasKey(value => value.Id);
        diagnostic.Property(value => value.Id).ValueGeneratedNever();
        diagnostic.Property(value => value.UploadTokenHash).HasMaxLength(32);
        diagnostic.Property(value => value.Status).HasConversion<string>().HasMaxLength(32);
        diagnostic.Property(value => value.AppVersion).HasMaxLength(64).IsRequired();
        diagnostic.Property(value => value.OsVersion).HasMaxLength(128).IsRequired();
        diagnostic.Property(value => value.Architecture).HasConversion<string>().HasMaxLength(32);
        diagnostic.Property(value => value.ErrorId).HasMaxLength(64);
        diagnostic.Property(value => value.IssueCategory).HasMaxLength(64);
        diagnostic.Property(value => value.StorageObjectKey).HasMaxLength(512);
        diagnostic.Property(value => value.ArchiveSha256).HasMaxLength(64);
        diagnostic.Property(value => value.ReferenceCode).HasMaxLength(32);
        diagnostic.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        diagnostic.HasOne<Installation>().WithMany().HasForeignKey(value => value.InstallationId).OnDelete(DeleteBehavior.Restrict);
        diagnostic.HasIndex(value => value.ExpiresAtUtc);
        diagnostic.HasIndex(value => new { value.Status, value.CreatedAtUtc });
        diagnostic.HasIndex(value => value.ReferenceCode).IsUnique().HasFilter("\"ReferenceCode\" IS NOT NULL");

        var access = modelBuilder.Entity<DiagnosticAccessEvent>();
        access.ToTable("DiagnosticAccessEvents");
        access.HasKey(value => value.Id);
        access.Property(value => value.Id).ValueGeneratedNever();
        access.Property(value => value.Action).HasMaxLength(64).IsRequired();
        access.HasOne<DiagnosticBundle>().WithMany().HasForeignKey(value => value.DiagnosticId).OnDelete(DeleteBehavior.Restrict);
        access.HasOne<AdminUser>().WithMany().HasForeignKey(value => value.AdminUserId).OnDelete(DeleteBehavior.Restrict);
        access.HasIndex(value => new { value.DiagnosticId, value.OccurredAtUtc });
    }

    private static void ConfigureAdministration(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<AdminUser>();
        user.ToTable("AdminUsers");
        user.HasKey(value => value.Id);
        user.Property(value => value.Id).ValueGeneratedNever();
        user.Property(value => value.Email).HasMaxLength(320).IsRequired();
        user.Property(value => value.PasswordHash).HasMaxLength(2048).IsRequired();
        user.Property(value => value.MfaSecretCiphertext).HasMaxLength(4096);
        user.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        user.HasIndex(value => value.Email).IsUnique();

        var role = modelBuilder.Entity<AdminRole>();
        role.ToTable("AdminRoles");
        role.HasKey(value => value.Id);
        role.Property(value => value.Id).ValueGeneratedNever();
        role.Property(value => value.Name).HasConversion<string>().HasMaxLength(64);
        role.HasIndex(value => value.Name).IsUnique();
        role.HasData(
            new AdminRole(Guid.Parse("10000000-0000-0000-0000-000000000001"), AdminRoleKind.Owner, true),
            new AdminRole(Guid.Parse("10000000-0000-0000-0000-000000000002"), AdminRoleKind.SecurityAdministrator, true),
            new AdminRole(Guid.Parse("10000000-0000-0000-0000-000000000003"), AdminRoleKind.OperationsAdministrator, true),
            new AdminRole(Guid.Parse("10000000-0000-0000-0000-000000000004"), AdminRoleKind.SupportAgent, true),
            new AdminRole(Guid.Parse("10000000-0000-0000-0000-000000000005"), AdminRoleKind.ReleaseManager, true),
            new AdminRole(Guid.Parse("10000000-0000-0000-0000-000000000006"), AdminRoleKind.ReadOnlyAnalyst, false));

        var userRole = modelBuilder.Entity<AdminUserRole>();
        userRole.ToTable("AdminUserRoles");
        userRole.HasKey(value => new { value.AdminUserId, value.AdminRoleId });
        userRole.HasOne<AdminUser>().WithMany().HasForeignKey(value => value.AdminUserId).OnDelete(DeleteBehavior.Cascade);
        userRole.HasOne<AdminRole>().WithMany().HasForeignKey(value => value.AdminRoleId).OnDelete(DeleteBehavior.Restrict);
        userRole.HasOne<AdminUser>().WithMany().HasForeignKey(value => value.GrantedByUserId).OnDelete(DeleteBehavior.Restrict);

        var recovery = modelBuilder.Entity<AdminRecoveryCode>();
        recovery.ToTable("AdminRecoveryCodes");
        recovery.HasKey(value => value.Id);
        recovery.Property(value => value.Id).ValueGeneratedNever();
        recovery.Property(value => value.CodeHash).HasMaxLength(32).IsRequired();
        recovery.HasOne<AdminUser>().WithMany().HasForeignKey(value => value.AdminUserId).OnDelete(DeleteBehavior.Cascade);
        recovery.HasIndex(value => new { value.AdminUserId, value.CodeHash }).IsUnique();

        var session = modelBuilder.Entity<AdminSession>();
        session.ToTable("AdminSessions");
        session.HasKey(value => value.Id);
        session.Property(value => value.Id).ValueGeneratedNever();
        session.Property(value => value.RefreshTokenHash).HasMaxLength(32).IsRequired();
        session.Property(value => value.UserAgentSummary).HasMaxLength(256).IsRequired();
        session.Property(value => value.ConcurrencyVersion).IsConcurrencyToken();
        session.HasOne<AdminUser>().WithMany().HasForeignKey(value => value.AdminUserId).OnDelete(DeleteBehavior.Cascade);
        session.HasOne<AdminSession>().WithMany().HasForeignKey(value => value.ReplacedBySessionId).OnDelete(DeleteBehavior.Restrict);
        session.HasIndex(value => value.RefreshTokenHash).IsUnique();
        session.HasIndex(value => new { value.AdminUserId, value.ExpiresAtUtc });

        var audit = modelBuilder.Entity<AuditEvent>();
        audit.ToTable("AuditEvents");
        audit.HasKey(value => value.Id);
        audit.Property(value => value.Id).ValueGeneratedNever();
        audit.Property(value => value.ActorId).HasMaxLength(128).IsRequired();
        audit.Property(value => value.ActorType).HasMaxLength(64).IsRequired();
        audit.Property(value => value.Action).HasMaxLength(128).IsRequired();
        audit.Property(value => value.TargetType).HasMaxLength(64).IsRequired();
        audit.Property(value => value.TargetId).HasMaxLength(128);
        audit.Property(value => value.Result).HasConversion<string>().HasMaxLength(32);
        audit.Property(value => value.IpRiskMetadata).HasMaxLength(512);
        audit.Property(value => value.UserAgentSummary).HasMaxLength(256);
        audit.Property(value => value.CorrelationId).HasMaxLength(128).IsRequired();
        audit.Property(value => value.Reason).HasMaxLength(512);
        audit.HasIndex(value => value.TimestampUtc);
        audit.HasIndex(value => new { value.Action, value.TimestampUtc });
        audit.HasIndex(value => value.CorrelationId);

        var retentionEvidence = modelBuilder.Entity<RetentionBatchEvidence>();
        retentionEvidence.ToTable("RetentionBatchEvidence");
        retentionEvidence.HasKey(value => value.Id);
        retentionEvidence.Property(value => value.Id).ValueGeneratedNever();
        retentionEvidence.Property(value => value.RecordType).HasConversion<string>().HasMaxLength(32);
        retentionEvidence.Property(value => value.PolicyVersion).HasMaxLength(64).IsRequired();
        retentionEvidence.Property(value => value.BatchDigestSha256).HasMaxLength(64).IsRequired();
        retentionEvidence.HasIndex(value => new { value.RecordType, value.ExecutedAtUtc });
        retentionEvidence.HasIndex(value => value.ExecutedAtUtc);

        var retentionPolicy = modelBuilder.Entity<RetentionPolicy>();
        retentionPolicy.ToTable("RetentionPolicies");
        retentionPolicy.HasKey(value => value.RecordType);
        retentionPolicy.Property(value => value.RecordType).HasConversion<string>().HasColumnType("text");
        retentionPolicy.Property(value => value.PolicyVersion).HasMaxLength(64).IsRequired();
    }

    private static void ConfigureInfrastructure(ModelBuilder modelBuilder)
    {
        var region = modelBuilder.Entity<InfrastructureRegion>();
        region.ToTable("InfrastructureRegions");
        region.HasKey(value => value.Id);
        region.Property(value => value.Id).ValueGeneratedNever();
        region.Property(value => value.Code).HasMaxLength(64).IsRequired();
        region.Property(value => value.DisplayName).HasMaxLength(128).IsRequired();
        region.HasIndex(value => value.Code).IsUnique();

        var health = modelBuilder.Entity<ServiceHealthSnapshot>();
        health.ToTable("ServiceHealthSnapshots");
        health.HasKey(value => value.Id);
        health.Property(value => value.Id).ValueGeneratedNever();
        health.Property(value => value.Service).HasMaxLength(128).IsRequired();
        health.Property(value => value.State).HasConversion<string>().HasMaxLength(32);
        health.HasOne<InfrastructureRegion>().WithMany().HasForeignKey(value => value.RegionId).OnDelete(DeleteBehavior.Restrict);
        health.HasIndex(value => new { value.RegionId, value.Service, value.ObservedAtUtc });
        health.HasIndex(value => value.ObservedAtUtc);

        var alert = modelBuilder.Entity<AlertEvent>();
        alert.ToTable("AlertEvents");
        alert.HasKey(value => value.Id);
        alert.Property(value => value.Id).ValueGeneratedNever();
        alert.Property(value => value.RuleName).HasMaxLength(128).IsRequired();
        alert.Property(value => value.Severity).HasConversion<string>().HasMaxLength(32);
        alert.Property(value => value.Region).HasMaxLength(64).IsRequired();
        alert.Property(value => value.Summary).HasMaxLength(512).IsRequired();
        alert.Property(value => value.RunbookUrl).HasMaxLength(512).IsRequired();
        alert.HasIndex(value => new { value.StartedAtUtc, value.Severity });
        alert.HasIndex(value => value.ResolvedAtUtc).HasFilter("\"ResolvedAtUtc\" IS NULL");
    }

    private void EnforceAppendOnlyRecords()
    {
        var prohibited = ChangeTracker.Entries()
            .FirstOrDefault(entry =>
                (entry.Entity is AuditEvent or DiagnosticAccessEvent or RetentionBatchEvidence or CustomerSecurityEvent &&
                 entry.State is EntityState.Modified or EntityState.Deleted) ||
                (entry.Entity is AlertEvent && entry.State == EntityState.Deleted) ||
                (entry.Entity is RetentionPolicy && entry.State is
                    EntityState.Added or EntityState.Modified or EntityState.Deleted));
        if (prohibited is not null)
            throw new InvalidOperationException($"{prohibited.Metadata.ClrType.Name} records are append-only and cannot be updated or deleted.");
    }
}
