using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Application.Security;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class PostgreSqlMigrationTests
{
    [Fact]
    public void ModelEnforcesIdentityIdempotencyAndAuditIndexes()
    {
        using var context = CreateContext("Host=localhost;Database=peeronq_model;Username=peeronq");
        var device = context.Model.FindEntityType(typeof(Device))!;
        var download = context.Model.FindEntityType(typeof(DownloadEvent))!;
        var audit = context.Model.FindEntityType(typeof(AuditEvent))!;
        var retentionEvidence = context.Model.FindEntityType(typeof(RetentionBatchEvidence))!;
        var retentionPolicy = context.Model.FindEntityType(typeof(RetentionPolicy))!;
        var session = context.Model.FindEntityType(typeof(RemoteSession))!;
        var customerAccount = context.Model.FindEntityType(typeof(CustomerAccount))!;
        var organizationMembership = context.Model.FindEntityType(typeof(OrganizationMembership))!;
        var invitation = context.Model.FindEntityType(typeof(OrganizationInvitation))!;
        var customerSecurityEvent = context.Model.FindEntityType(typeof(CustomerSecurityEvent))!;

        Assert.Contains(device.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(Device.PublicDeviceIdHash));
        Assert.Contains(device.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(Device.IdentityFingerprint));
        Assert.Contains(download.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(DownloadEvent.IdempotencyKeyHash));
        Assert.Contains(audit.GetIndexes(), index => index.Properties.Any(property => property.Name == nameof(AuditEvent.TimestampUtc)));
        Assert.Contains(retentionEvidence.GetIndexes(), index => index.Properties.Any(property => property.Name == nameof(RetentionBatchEvidence.ExecutedAtUtc)));
        Assert.Equal(nameof(RetentionPolicy.RecordType), retentionPolicy.FindPrimaryKey()!.Properties.Single().Name);
        Assert.False(session.FindProperty(nameof(RemoteSession.LastActivityAtUtc))!.IsNullable);
        Assert.Contains(session.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(RemoteSession.Lifecycle), nameof(RemoteSession.LastActivityAtUtc)]));
        Assert.Contains(customerAccount.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(CustomerAccount.Email));
        Assert.Equal(2, organizationMembership.FindPrimaryKey()!.Properties.Count);
        Assert.Contains(invitation.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(OrganizationInvitation.TokenHash));
        Assert.Contains(customerSecurityEvent.GetIndexes(), index => index.Properties.Any(property => property.Name == nameof(CustomerSecurityEvent.OrganizationId)));
    }

    [Fact]
    public void GeneratedPostgreSqlScriptContainsCompleteForwardSchema()
    {
        using var context = CreateContext("Host=localhost;Database=peeronq_script;Username=peeronq");
        var migrator = context.GetService<IMigrator>();

        var script = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("CREATE TABLE \"Devices\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"Installations\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"RemoteSessions\"", script, StringComparison.Ordinal);
        Assert.Contains("SET \"LastActivityAtUtc\" = COALESCE(\"ConnectedAtUtc\", \"StartedAtUtc\")", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"AuditEvents\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_DownloadEvents_IdempotencyKeyHash\"", script, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO \"AdminRoles\"", script, StringComparison.Ordinal);
        Assert.Contains("TR_AuditEvents_AppendOnly", script, StringComparison.Ordinal);
        Assert.Contains("TR_DiagnosticAccessEvents_AppendOnly", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"RetentionBatchEvidence\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"CustomerAccounts\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"Organizations\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"OrganizationMemberships\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"OrganizationInvitations\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"OrganizationPolicies\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_OrganizationInvitations_TokenHash\"", script, StringComparison.Ordinal);
        Assert.Contains("TR_CustomerSecurityEvents_AppendOnly", script, StringComparison.Ordinal);
        Assert.Contains("peeronq_apply_audit_retention", script, StringComparison.Ordinal);
        Assert.Contains("peeronq_apply_alert_retention", script, StringComparison.Ordinal);
        Assert.Contains("SECURITY DEFINER", script, StringComparison.Ordinal);
        Assert.Contains("SET search_path = pg_catalog, public", script, StringComparison.Ordinal);
        Assert.Contains("SET ROLE peeronq_retention_executor", script, StringComparison.Ordinal);
        Assert.Contains("RESET ROLE", script, StringComparison.Ordinal);
        Assert.Contains("REVOKE CREATE ON SCHEMA public FROM peeronq_retention_executor", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE public.\"RetentionPolicies\"", script, StringComparison.Ordinal);
        Assert.Contains("clock_timestamp()", script, StringComparison.Ordinal);
        Assert.Contains("Policy, clock and cutoff are owned by the database", script, StringComparison.Ordinal);
        Assert.Contains("TR_AlertEvents_GovernedDelete", script, StringComparison.Ordinal);
        Assert.Contains("TR_RetentionBatchEvidence_AppendOnly", script, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureCreated", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContextRejectsAuditAndDiagnosticAccessMutationBeforeDatabaseWrite()
    {
        await using var context = CreateContext("Host=localhost;Database=peeronq_append_only;Username=peeronq");
        var audit = new AuditEvent(Guid.NewGuid(), "admin", "Admin", "Read", "Device", null,
            AuditResult.Succeeded, DateTimeOffset.UtcNow, null, null, "correlation", null);
        context.Attach(audit);
        context.Entry(audit).State = EntityState.Modified;

        var auditError = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains(nameof(AuditEvent), auditError.Message, StringComparison.Ordinal);

        context.ChangeTracker.Clear();
        var access = new DiagnosticAccessEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Read", DateTimeOffset.UtcNow);
        context.Attach(access);
        context.Entry(access).State = EntityState.Deleted;

        var accessError = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains(nameof(DiagnosticAccessEvent), accessError.Message, StringComparison.Ordinal);

        context.ChangeTracker.Clear();
        var evidence = (RetentionBatchEvidence)Activator.CreateInstance(typeof(RetentionBatchEvidence), nonPublic: true)!;
        context.Attach(evidence);
        context.Entry(evidence).State = EntityState.Modified;
        var evidenceError = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains(nameof(RetentionBatchEvidence), evidenceError.Message, StringComparison.Ordinal);

        context.ChangeTracker.Clear();
        var customerEvent = new CustomerSecurityEvent(Guid.NewGuid(), Guid.NewGuid(), null, "account.test",
            AuditResult.Succeeded, DateTimeOffset.UtcNow, "correlation", null);
        context.Attach(customerEvent);
        context.Entry(customerEvent).State = EntityState.Modified;
        var customerEventError = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains(nameof(CustomerSecurityEvent), customerEventError.Message, StringComparison.Ordinal);

        context.ChangeTracker.Clear();
        var alert = new AlertEvent(Guid.NewGuid(), "test", AlertSeverity.Warning, "eu-west", "summary", "https://runbook.example.test", DateTimeOffset.UtcNow);
        context.Attach(alert);
        context.Entry(alert).State = EntityState.Deleted;
        var alertError = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains(nameof(AlertEvent), alertError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceModelContainsNoProhibitedContentColumns()
    {
        using var context = CreateContext("Host=localhost;Database=peeronq_privacy;Username=peeronq");
        var names = context.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()).Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Screen", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name =>
            name.Contains("Clipboard", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("ClipboardAllowed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Keystroke", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("RawIpAddress", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("RefreshToken", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("UploadToken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task MigrationAppliesToExplicitPostgreSqlTestDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var context = CreateContext(connectionString, enableRetry: true);
        await MigrateForTestAsync(context, connectionString);

        Assert.Contains(context.Database.GetAppliedMigrations(), value => value.EndsWith("_InitialCloudPlatform", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetAppliedMigrations(), value => value.EndsWith("_AddGovernedRetention", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetAppliedMigrations(), value => value.EndsWith("_ProofBoundDeviceEnrollment", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetAppliedMigrations(), value => value.EndsWith("_HardenGovernedRetentionExecution", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetAppliedMigrations(), value => value.EndsWith("_EnforceDatabaseRetentionPolicies", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetAppliedMigrations(), value => value.EndsWith("_AddSessionActivityHeartbeat", StringComparison.Ordinal));
        Assert.True(await context.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM \"Devices\" LIMIT 0").ToListAsync() is not null);
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task NoInheritMigratorCanUpgradeFunctionsAlreadyOwnedByRetentionExecutor()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N")[..16];
        var migratorRole = $"peeronq_migrator_{suffix}";
        var databaseName = $"peeronq_migration_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(migratorRole);
        var quotedDatabase = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        var roleCreated = false;
        var databaseCreated = false;
        try
        {
            await ExecuteNonQueryAsync(admin,
                $"CREATE ROLE {quotedRole} LOGIN PASSWORD '{password}' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
            roleCreated = true;
            await ExecuteNonQueryAsync(admin, $"GRANT peeronq_retention_executor TO {quotedRole}");
            await ExecuteNonQueryAsync(admin, $"CREATE DATABASE {quotedDatabase} OWNER {quotedRole}");
            databaseCreated = true;

            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = databaseName,
                Username = migratorRole,
                Password = password,
                Pooling = false,
            };
            await using var context = CreateContext(builder.ConnectionString);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260811130500_HardenGovernedRetentionExecution");
            Assert.Equal("peeronq_retention_executor", await GetAuditRetentionOwnerAsync(context));

            await migrator.MigrateAsync();

            Assert.Contains(context.Database.GetAppliedMigrations(), value =>
                value.EndsWith("_EnforceDatabaseRetentionPolicies", StringComparison.Ordinal));
            Assert.Equal("peeronq_retention_executor", await GetAuditRetentionOwnerAsync(context));
            Assert.False(await context.Database.SqlQueryRaw<bool>(
                "SELECT pg_catalog.has_schema_privilege('peeronq_retention_executor', 'public', 'CREATE') AS \"Value\"")
                .SingleAsync());
            Assert.True(await context.Database.SqlQueryRaw<bool>(
                "SELECT to_regclass('public.\"RetentionPolicies\"') IS NOT NULL AS \"Value\"")
                .SingleAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await ExecuteNonQueryAsync(admin, $"DROP DATABASE IF EXISTS {quotedDatabase} WITH (FORCE)");
            if (roleCreated)
            {
                await ExecuteNonQueryAsync(admin, $"REVOKE peeronq_retention_executor FROM {quotedRole}");
                await ExecuteNonQueryAsync(admin, $"DROP ROLE IF EXISTS {quotedRole}");
            }
        }
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task DiagnosticRetentionClaimsThenFinalizesWithoutLosingDeleteKey()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var context = CreateContext(connectionString, enableRetry: true);
        await MigrateForTestAsync(context, connectionString);
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        var installation = Installation.Register(Guid.NewGuid(), System.Security.Cryptography.SHA256.HashData("retention-install"u8),
            PlatformKind.Windows, ArchitectureKind.X64, "1.0.0", "Windows 11", InstallChannel.Stable, "1", "eu-west", old);
        context.Installations.Add(installation);
        var rawToken = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var diagnostic = DiagnosticBundle.Create(installation.Id, System.Security.Cryptography.SHA256.HashData(rawToken),
            old.AddMinutes(15), old.AddDays(14), "1.0.0", "Windows 11", ArchitectureKind.X64, null, "network", 3, old);
        diagnostic.MarkUploaded(System.Security.Cryptography.SHA256.HashData(rawToken), 1024, new string('a', 64),
            $"diagnostics/{diagnostic.Id:N}.zip", diagnostic.Id.ToString("N"), old.AddMinutes(1));
        context.DiagnosticBundles.Add(diagnostic);
        await context.SaveChangesAsync();
        var repository = (IRetentionRepository)new CloudRepository(context, new TestHostEnvironment());

        var claims = await repository.ClaimExpiredDiagnosticsAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);
        var claim = Assert.Single(claims, value => value.DiagnosticId == diagnostic.Id);
        Assert.Equal($"diagnostics/{diagnostic.Id:N}.zip", claim.StorageObjectKey);

        await repository.CompleteDiagnosticExpirationAsync(diagnostic.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        context.ChangeTracker.Clear();
        var expired = await context.DiagnosticBundles.SingleAsync(value => value.Id == diagnostic.Id);
        Assert.Equal(DiagnosticStatus.Expired, expired.Status);
        Assert.Null(expired.StorageObjectKey);
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task EndedHeartbeatSessionRemainsEligibleForSessionRetention()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var context = CreateContext(connectionString);
        await MigrateForTestAsync(context, connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-200);
        var viewer = Device.Create(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()),
            "111-***-***-111", "Viewer", new string('a', 64), old);
        var host = Device.Create(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()),
            "222-***-***-222", "Host", new string('b', 64), old);
        context.Devices.AddRange(viewer, host);
        var session = RemoteSession.Start(Guid.NewGuid(), viewer.Id, host.Id, PermissionMode.ViewOnly,
            "eu-west", "1.0.0", "1.0.0", old);
        session.MarkConnected(ConnectionPath.InternetDirect, false, old.AddMinutes(1), old.AddMinutes(1));
        session.RecordActivity(viewer.Id, Guid.NewGuid(), old.AddMinutes(2));
        session.End(SessionEndReason.Completed, null, null, 0, old.AddMinutes(3));
        context.RemoteSessions.Add(session);
        await context.SaveChangesAsync();
        var repository = (IRetentionRepository)new CloudRepository(context, new TestHostEnvironment());

        var deleted = await repository.DeleteExpiredSessionMetadataAsync(
            now.AddDays(-180), 10, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.False(await context.RemoteSessions.AnyAsync(value => value.Id == session.Id));
        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task OverviewAndDistributionsUseConnectedSessionsDistinctDevicesAndActiveInstallations()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var context = CreateContext(connectionString);
        await MigrateForTestAsync(context, connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow;
        var hashA = System.Security.Cryptography.SHA256.HashData("overview-device-a"u8);
        var hashB = System.Security.Cryptography.SHA256.HashData("overview-device-b"u8);
        var deviceA = Device.Create(hashA, "111-***-***-111", "Device A", new string('a', 64), now);
        var deviceB = Device.Create(hashB, "222-***-***-222", "Device B", new string('b', 64), now);
        context.Devices.AddRange(deviceA, deviceB);

        var installationA1 = CreateAttachedInstallation(hashA, deviceA.Id, "1.0.0", "Windows 11", now);
        var installationA2 = CreateAttachedInstallation(hashA, deviceA.Id, "1.0.0", "Windows 11", now.AddMinutes(-5));
        var installationB = CreateAttachedInstallation(hashB, deviceB.Id, "2.0.0", "Windows 10", now.AddMinutes(-10));
        var unregistered = CreateAttachedInstallation(hashB, deviceB.Id, "0.9.0", "Windows 10", now.AddDays(-40));
        unregistered.Unregister(now.AddDays(-30));
        context.Installations.AddRange(installationA1, installationA2, installationB, unregistered);

        var starting = RemoteSession.Start(Guid.NewGuid(), deviceA.Id, deviceB.Id, PermissionMode.ViewOnly,
            "eu-west", "1.0.0", "2.0.0", now.AddMinutes(-3));
        var connected = RemoteSession.Start(Guid.NewGuid(), deviceB.Id, deviceA.Id, PermissionMode.FullControl,
            "eu-west", "2.0.0", "1.0.0", now.AddMinutes(-2));
        connected.MarkConnected(ConnectionPath.InternetDirect, false, now.AddMinutes(-1));
        context.RemoteSessions.AddRange(starting, connected);

        var rawToken = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        context.DiagnosticBundles.Add(DiagnosticBundle.Create(installationA1.Id,
            System.Security.Cryptography.SHA256.HashData(rawToken), now.AddMinutes(15), now.AddDays(14),
            "1.0.0", "Windows 11", ArchitectureKind.X64, null, "crash", 3, now));
        var duplicateCrashToken = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        context.DiagnosticBundles.Add(DiagnosticBundle.Create(installationA1.Id,
            System.Security.Cryptography.SHA256.HashData(duplicateCrashToken), now.AddMinutes(15), now.AddDays(14),
            "1.0.0", "Windows 11", ArchitectureKind.X64, null, "crash", 3, now.AddSeconds(1)));
        var completedDownload = DownloadEvent.Start(Guid.NewGuid(),
            System.Security.Cryptography.SHA256.HashData("overview-download-completed"u8),
            System.Security.Cryptography.SHA256.HashData("overview-download-unique-a"u8),
            PlatformKind.Windows, ArchitectureKind.X64, "1.0.0", InstallChannel.Development,
            "website", null, null, "Chrome", now.AddMinutes(-4));
        completedDownload.Complete(DownloadResult.Completed, now.AddMinutes(-3));
        var partialDownload = DownloadEvent.Start(Guid.NewGuid(),
            System.Security.Cryptography.SHA256.HashData("overview-download-partial"u8),
            System.Security.Cryptography.SHA256.HashData("overview-download-unique-b"u8),
            PlatformKind.Windows, ArchitectureKind.Arm64, "1.0.0", InstallChannel.Development,
            "website", null, null, "Chrome", now.AddMinutes(-2));
        partialDownload.Complete(DownloadResult.Partial, now.AddMinutes(-1));
        var startedDownload = DownloadEvent.Start(Guid.NewGuid(),
            System.Security.Cryptography.SHA256.HashData("overview-download-started"u8), null,
            PlatformKind.Windows, ArchitectureKind.X64, "1.0.0", InstallChannel.Development,
            "website", null, null, "Chrome", now);
        context.DownloadEvents.AddRange(completedDownload, partialDownload, startedDownload);
        await context.SaveChangesAsync();

        var repository = (IAnalyticsQueryRepository)new CloudRepository(context, new TestHostEnvironment());
        var overview = await repository.GetOverviewAsync(now, 2, CancellationToken.None);
        var distributions = await repository.GetVersionDistributionsAsync(now, 1, CancellationToken.None);

        Assert.Equal(4, overview.TotalInstallations);
        Assert.Equal(3, overview.ActiveInstallations);
        Assert.Equal(3, overview.TotalDownloads);
        Assert.Equal(1, overview.CompletedDownloads);
        Assert.Equal(2, overview.UniqueDownloadEstimate);
        Assert.Equal(2, overview.ActiveDevicesToday);
        Assert.Equal(1, overview.ActiveSessions);
        Assert.Equal(33.33m, overview.CrashRate);
        Assert.Equal(3, distributions.ActiveInstallations);
        Assert.Equal(3, distributions.ActiveWindowsInstallations);
        Assert.Collection(distributions.ClientVersions,
            bucket => { Assert.Equal("1.0.0", bucket.Label); Assert.Equal(2, bucket.Count); Assert.Equal(66.67m, bucket.Percentage); },
            bucket => { Assert.Equal("Other", bucket.Label); Assert.Equal(1, bucket.Count); Assert.Equal(33.33m, bucket.Percentage); });
        Assert.Collection(distributions.WindowsVersions,
            bucket => { Assert.Equal("Windows 11", bucket.Label); Assert.Equal(2, bucket.Count); },
            bucket => { Assert.Equal("Other", bucket.Label); Assert.Equal(1, bucket.Count); });

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task PostgreSqlTriggerRejectsAuditMutation()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var context = CreateContext(connectionString);
        await MigrateForTestAsync(context, connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var audit = new AuditEvent(Guid.NewGuid(), "admin", "Admin", "Read", "Device", null,
            AuditResult.Succeeded, DateTimeOffset.UtcNow, null, null, "trigger-test", null);
        context.AuditEvents.Add(audit);
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE \"AuditEvents\" SET \"Reason\" = 'tampered' WHERE \"Id\" = {0}", audit.Id));

        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task GovernedRetentionIsBoundedHonorsLegalHoldAndWritesImmutableEvidence()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var context = CreateContext(connectionString);
        await MigrateForTestAsync(context, connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-3000);
        var audits = Enumerable.Range(0, 3).Select(index => new AuditEvent(Guid.NewGuid(), "retention-worker", "System",
            "RetentionTest", "AuditEvent", index.ToString(), AuditResult.Succeeded, old.AddMinutes(index), null, null,
            $"retention-{Guid.NewGuid():N}", null)).ToArray();
        var recentAudit = new AuditEvent(Guid.NewGuid(), "retention-worker", "System", "RetentionTest", "AuditEvent",
            "recent", AuditResult.Succeeded, now.AddDays(-30), null, null, $"retention-{Guid.NewGuid():N}", null);
        var resolvedOldAlert = new AlertEvent(Guid.NewGuid(), "old-alert", AlertSeverity.Warning, "eu-west", "old",
            "https://runbook.example.test/old", old);
        resolvedOldAlert.Resolve(old.AddDays(1));
        var unresolvedOldAlert = new AlertEvent(Guid.NewGuid(), "active-alert", AlertSeverity.Critical, "eu-west", "active",
            "https://runbook.example.test/active", old);
        var resolvedRecentAlert = new AlertEvent(Guid.NewGuid(), "recent-alert", AlertSeverity.Information, "eu-west", "recent",
            "https://runbook.example.test/recent", now.AddDays(-10));
        resolvedRecentAlert.Resolve(now.AddDays(-9));
        context.AuditEvents.AddRange(audits.Append(recentAudit));
        context.AlertEvents.AddRange(resolvedOldAlert, unresolvedOldAlert, resolvedRecentAlert);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync(
            """
            UPDATE public."RetentionPolicies"
            SET "PolicyVersion" = 'test-v1', "RetentionDays" = 365,
                "Enabled" = true, "LegalHold" = false, "UpdatedAtUtc" = clock_timestamp()
            """);
        var repository = (IRetentionRepository)new CloudRepository(context, new TestHostEnvironment());
        var evidenceCountBefore = await context.RetentionBatchEvidenceEntries.CountAsync();

        var held = await repository.ApplyRetentionBatchAsync(new RetentionBatchPolicy(
            RetentionRecordKind.AuditEvents, "test-v1", TimeSpan.FromDays(365), true, true), now, 2, CancellationToken.None);
        Assert.True(held.Skipped);
        Assert.Equal("legal_hold", held.SkipReason);
        Assert.Equal(evidenceCountBefore, await context.RetentionBatchEvidenceEntries.CountAsync());

        var auditResult = await repository.ApplyRetentionBatchAsync(new RetentionBatchPolicy(
            RetentionRecordKind.AuditEvents, "test-v1", TimeSpan.FromDays(365), true, false), now, 2, CancellationToken.None);
        var alertResult = await repository.ApplyRetentionBatchAsync(new RetentionBatchPolicy(
            RetentionRecordKind.AlertEvents, "test-v1", TimeSpan.FromDays(365), true, false), now, 10, CancellationToken.None);
        context.ChangeTracker.Clear();

        Assert.Equal(2, auditResult.DeletedCount);
        Assert.NotNull(auditResult.EvidenceId);
        Assert.Equal(1, alertResult.DeletedCount);
        Assert.NotNull(alertResult.EvidenceId);
        var testAuditIds = audits.Select(item => item.Id).Append(recentAudit.Id).ToArray();
        Assert.Equal(2, await context.AuditEvents.CountAsync(value => testAuditIds.Contains(value.Id)));
        Assert.True(await context.AlertEvents.AnyAsync(value => value.Id == unresolvedOldAlert.Id));
        Assert.True(await context.AlertEvents.AnyAsync(value => value.Id == resolvedRecentAlert.Id));
        Assert.False(await context.AlertEvents.AnyAsync(value => value.Id == resolvedOldAlert.Id));
        var evidenceIds = new[] { auditResult.EvidenceId!.Value, alertResult.EvidenceId!.Value };
        var evidenceRows = await context.RetentionBatchEvidenceEntries
            .Where(value => evidenceIds.Contains(value.Id))
            .OrderBy(value => value.RecordType)
            .ToListAsync();
        Assert.Equal(2, evidenceRows.Count);
        Assert.All(evidenceRows, value =>
        {
            Assert.Equal("test-v1", value.PolicyVersion);
            Assert.InRange(value.DeletedCount, 1, value.RequestedBatchSize);
            Assert.Equal(64, value.BatchDigestSha256.Length);
        });

        await transaction.CreateSavepointAsync("before_direct_audit_delete");
        var auditDeleteError = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"AuditEvents\" WHERE \"Id\" = {0}", recentAudit.Id));
        Assert.Equal("42501", auditDeleteError.SqlState);
        await transaction.RollbackToSavepointAsync("before_direct_audit_delete");

        await transaction.CreateSavepointAsync("before_direct_alert_delete");
        var alertDeleteError = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"AlertEvents\" WHERE \"Id\" = {0}", unresolvedOldAlert.Id));
        Assert.Equal("42501", alertDeleteError.SqlState);
        await transaction.RollbackToSavepointAsync("before_direct_alert_delete");

        var evidenceUpdateError = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE \"RetentionBatchEvidence\" SET \"PolicyVersion\" = 'tampered' WHERE \"Id\" = {0}", auditResult.EvidenceId!.Value));
        Assert.Equal("42501", evidenceUpdateError.SqlState);
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task RuntimeRoleCanExecuteGovernedRetentionButCannotMutateTablesOrSchema()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var context = CreateContext(connectionString);
        await MigrateForTestAsync(context, connectionString);
        var now = DateTimeOffset.UtcNow;
        var audit = new AuditEvent(Guid.NewGuid(), "retention-test", "System", "Delete", "AuditEvent",
            null, AuditResult.Succeeded, now.AddDays(-10000), null, null, $"retention-{Guid.NewGuid():N}", null);
        var recentAudit = new AuditEvent(Guid.NewGuid(), "retention-test", "System", "Delete", "AuditEvent",
            null, AuditResult.Succeeded, now.AddDays(-30), null, null, $"retention-{Guid.NewGuid():N}", null);
        context.AuditEvents.AddRange(audit, recentAudit);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync(
            """
            UPDATE public."RetentionPolicies"
            SET "PolicyVersion" = 'phase6-v1', "RetentionDays" = 365,
                "Enabled" = true, "LegalHold" = false, "UpdatedAtUtc" = clock_timestamp()
            WHERE "RecordType" = 'AuditEvents'
            """);

        var runtimeRole = $"peeronq_runtime_test_{Guid.NewGuid():N}";
        var quotedRuntimeRole = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRole);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            await ExecuteNonQueryAsync(connection, $"CREATE ROLE {quotedRuntimeRole} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
            await ExecuteNonQueryAsync(connection, $"GRANT USAGE ON SCHEMA public TO {quotedRuntimeRole}");
            await ExecuteNonQueryAsync(connection,
                $"GRANT EXECUTE ON FUNCTION peeronq_apply_audit_retention(uuid, text, timestamp with time zone, integer, timestamp with time zone) TO {quotedRuntimeRole}");
            await ExecuteNonQueryAsync(connection, $"SET ROLE {quotedRuntimeRole}");

            var directUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(connection, "UPDATE public.\"AuditEvents\" SET \"Reason\" = 'tampered'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, directUpdate.SqlState);
            var directDelete = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(connection, "DELETE FROM public.\"AuditEvents\""));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, directDelete.SqlState);
            var directPolicyRead = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(connection, "SELECT * FROM public.\"RetentionPolicies\""));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, directPolicyRead.SqlState);

            await using var governed = connection.CreateCommand();
            governed.CommandText =
                "SELECT peeronq_apply_audit_retention(@evidence, 'attacker-policy', @cutoff, 1, @executed)";
            var evidenceId = Guid.NewGuid();
            governed.Parameters.AddWithValue("evidence", evidenceId);
            governed.Parameters.AddWithValue("cutoff", now);
            governed.Parameters.AddWithValue("executed", new DateTimeOffset(3000, 1, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.Equal(1, Convert.ToInt32(await governed.ExecuteScalarAsync()));
            await ExecuteNonQueryAsync(connection, "RESET ROLE");

            await using var privilege = connection.CreateCommand();
            privilege.CommandText =
                "SELECT pg_catalog.has_schema_privilege('peeronq_retention_executor', 'public', 'CREATE')";
            Assert.False(Convert.ToBoolean(await privilege.ExecuteScalarAsync()));
            privilege.CommandText =
                "SELECT pg_catalog.has_table_privilege('peeronq_retention_executor', 'public.\"RetentionPolicies\"', 'SELECT')";
            Assert.True(Convert.ToBoolean(await privilege.ExecuteScalarAsync()));
            privilege.CommandText =
                "SELECT pg_catalog.has_table_privilege('peeronq_retention_executor', 'public.\"RetentionPolicies\"', 'UPDATE')";
            Assert.False(Convert.ToBoolean(await privilege.ExecuteScalarAsync()));

            context.ChangeTracker.Clear();
            Assert.False(await context.AuditEvents.AnyAsync(value => value.Id == audit.Id));
            Assert.True(await context.AuditEvents.AnyAsync(value => value.Id == recentAudit.Id));
            var evidence = await context.RetentionBatchEvidenceEntries.SingleAsync(value => value.Id == evidenceId);
            Assert.Equal(1, evidence.DeletedCount);
            Assert.Equal(64, evidence.BatchDigestSha256.Length);
            Assert.Equal("phase6-v1", evidence.PolicyVersion);
            Assert.InRange(evidence.ExecutedAtUtc, now.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.InRange(evidence.CutoffUtc, now.AddDays(-365).AddMinutes(-1), now.AddDays(-365).AddMinutes(1));

            var heldAudit = new AuditEvent(Guid.NewGuid(), "retention-test", "System", "Delete", "AuditEvent",
                null, AuditResult.Succeeded, now.AddDays(-500), null, null, $"retention-{Guid.NewGuid():N}", null);
            context.AuditEvents.Add(heldAudit);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync(
                """
                UPDATE public."RetentionPolicies"
                SET "Enabled" = true, "LegalHold" = true, "UpdatedAtUtc" = clock_timestamp()
                WHERE "RecordType" = 'AuditEvents'
                """);
            await ExecuteNonQueryAsync(connection, $"SET ROLE {quotedRuntimeRole}");
            var heldEvidenceId = Guid.NewGuid();
            Assert.Equal(0, await ExecuteGovernedAuditRetentionAsync(connection, heldEvidenceId, now, now));
            await ExecuteNonQueryAsync(connection, "RESET ROLE");

            await context.Database.ExecuteSqlRawAsync(
                """
                UPDATE public."RetentionPolicies"
                SET "Enabled" = false, "LegalHold" = false, "UpdatedAtUtc" = clock_timestamp()
                WHERE "RecordType" = 'AuditEvents'
                """);
            await ExecuteNonQueryAsync(connection, $"SET ROLE {quotedRuntimeRole}");
            var disabledEvidenceId = Guid.NewGuid();
            Assert.Equal(0, await ExecuteGovernedAuditRetentionAsync(connection, disabledEvidenceId, now, now));
            await ExecuteNonQueryAsync(connection, "RESET ROLE");

            context.ChangeTracker.Clear();
            Assert.True(await context.AuditEvents.AnyAsync(value => value.Id == heldAudit.Id));
            Assert.False(await context.RetentionBatchEvidenceEntries.AnyAsync(value =>
                value.Id == heldEvidenceId || value.Id == disabledEvidenceId));
        }
        finally
        {
            try { await ExecuteNonQueryAsync(connection, "RESET ROLE"); } catch (PostgresException) { }
            await ExecuteNonQueryAsync(connection,
                """
                UPDATE public."RetentionPolicies"
                SET "PolicyVersion" = 'phase6-v1', "RetentionDays" = 365,
                    "Enabled" = false, "LegalHold" = false, "UpdatedAtUtc" = clock_timestamp()
                WHERE "RecordType" = 'AuditEvents'
                """);
            await ExecuteNonQueryAsync(connection,
                $"REVOKE ALL PRIVILEGES ON SCHEMA public FROM {quotedRuntimeRole}");
            await ExecuteNonQueryAsync(connection,
                $"REVOKE ALL ON FUNCTION peeronq_apply_audit_retention(uuid, text, timestamp with time zone, integer, timestamp with time zone) FROM {quotedRuntimeRole}");
            await ExecuteNonQueryAsync(connection, $"DROP ROLE IF EXISTS {quotedRuntimeRole}");
        }
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task ProofBootstrapIsAtomicStableCollisionSafeAndMigratesLegacyAlias()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var options = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        await using (var migrationContext = new CloudDbContext(options))
            await MigrateForTestAsync(migrationContext, connectionString);
        var factory = new PooledDbContextFactory<CloudDbContext>(options);
        var publicIds = new PublicDeviceIdService(1,
            Enumerable.Repeat((byte)17, 32).ToArray(), null);
        var store = new DeviceBootstrapStore(factory, publicIds);
        var now = DateTimeOffset.UtcNow;
        var fingerprint = new string('1', 64);
        var installationId = Guid.NewGuid();
        var request = BootstrapRequest(installationId, fingerprint, now);

        await using (var before = await factory.CreateDbContextAsync())
        {
            Assert.False(await before.Devices.AnyAsync(value => value.IdentityFingerprint == fingerprint));
            Assert.False(await before.Installations.AnyAsync(value => value.Id == installationId));
        }

        var raced = await Task.WhenAll(
            store.CompleteAsync(request, CancellationToken.None),
            store.CompleteAsync(request, CancellationToken.None));
        Assert.Equal(raced[0].PublicDeviceId, raced[1].PublicDeviceId);
        Assert.Equal(raced[0].DeviceId, raced[1].DeviceId);

        var collisionFingerprint = new string('2', 64);
        var collisionAlias = publicIds.DeriveFromFingerprint(collisionFingerprint, 0);
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Devices.Add(Device.Create(collisionAlias.LookupHash, collisionAlias.MaskedValue, "Occupied",
                new string('3', 64), collisionAlias.CollisionCounter, collisionAlias.KeyVersion, now));
            await seed.SaveChangesAsync();
        }
        var collisionStore = new DeviceBootstrapStore(factory, publicIds);
        var collisionInstallationId = Guid.NewGuid();
        var collision = await collisionStore.CompleteAsync(
            BootstrapRequest(collisionInstallationId, collisionFingerprint, now), CancellationToken.None);

        var legacyFingerprint = new string('4', 64);
        var legacyHash = System.Security.Cryptography.SHA256.HashData("legacy-random-alias"u8);
        var legacyInstallationId = Guid.NewGuid();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var legacyDevice = Device.Create(legacyHash, "111-***-***-111", "Legacy",
                legacyFingerprint, now);
            var legacyInstallation = Installation.Register(legacyInstallationId, legacyHash,
                PlatformKind.Windows, ArchitectureKind.X64, "1.0.0", "Windows 11",
                InstallChannel.Stable, "1", "eu-west", now);
            legacyInstallation.AttachDevice(legacyDevice.Id, legacyHash, now);
            seed.AddRange(legacyDevice, legacyInstallation);
            await seed.SaveChangesAsync();
        }
        var migrated = await store.CompleteAsync(
            BootstrapRequest(legacyInstallationId, legacyFingerprint, now.AddMinutes(1)),
            CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Devices.CountAsync(value => value.IdentityFingerprint == fingerprint));
        Assert.Equal(1, await verify.Installations.CountAsync(value => value.Id == installationId));
        var collidedDevice = await verify.Devices.SingleAsync(value => value.Id == collision.DeviceId);
        Assert.Equal(1, collidedDevice.PublicDeviceIdCollisionCounter);
        var migratedDevice = await verify.Devices.SingleAsync(value => value.Id == migrated.DeviceId);
        var migratedInstallation = await verify.Installations.SingleAsync(value => value.Id == legacyInstallationId);
        Assert.True(migratedDevice.PublicDeviceIdCollisionCounter >= 0);
        Assert.True(migratedDevice.PublicDeviceIdKeyVersion > 0);
        Assert.Equal(1, migratedInstallation.ProofBindingVersion);
        Assert.NotEqual("111-111-111-111", migrated.PublicDeviceId);
        Assert.NotEqual(migrated.PublicDeviceId, migratedDevice.MaskedPublicDeviceId);

        await verify.Installations.Where(value =>
            value.Id == installationId || value.Id == collisionInstallationId || value.Id == legacyInstallationId)
            .ExecuteDeleteAsync();
        var testFingerprints = new[] { fingerprint, collisionFingerprint, new string('3', 64), legacyFingerprint };
        await verify.Devices.Where(value => testFingerprints.Contains(value.IdentityFingerprint))
            .ExecuteDeleteAsync();
    }

    private static DeviceBootstrapRequest BootstrapRequest(
        Guid installationId,
        string fingerprint,
        DateTimeOffset now) => new(
        installationId,
        fingerprint,
        "Device",
        PlatformKind.Windows,
        ArchitectureKind.X64,
        "1.0.0",
        "Windows 11",
        InstallChannel.Stable,
        "1",
        "eu-west",
        now);

    private static async Task MigrateForTestAsync(CloudDbContext context, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection,
            """
            DO $peeronq$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'peeronq_retention_executor') THEN
                    CREATE ROLE peeronq_retention_executor NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                END IF;
            END;
            $peeronq$;
            """);
        await context.Database.MigrateAsync();
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecuteGovernedAuditRetentionAsync(
        NpgsqlConnection connection,
        Guid evidenceId,
        DateTimeOffset cutoffUtc,
        DateTimeOffset executedAtUtc)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT peeronq_apply_audit_retention(@evidence, 'untrusted-policy', @cutoff, 10, @executed)";
        command.Parameters.AddWithValue("evidence", evidenceId);
        command.Parameters.AddWithValue("cutoff", cutoffUtc);
        command.Parameters.AddWithValue("executed", executedAtUtc);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static Task<string> GetAuditRetentionOwnerAsync(CloudDbContext context) =>
        context.Database.SqlQueryRaw<string>(
            """
            SELECT pg_catalog.pg_get_userbyid(p.proowner) AS "Value"
            FROM pg_catalog.pg_proc p
            WHERE p.proname = 'peeronq_apply_audit_retention'
            ORDER BY p.oid DESC
            LIMIT 1
            """).SingleAsync();

    private static Installation CreateAttachedInstallation(byte[] publicIdHash, Guid deviceId, string appVersion, string osVersion, DateTimeOffset seenAtUtc)
    {
        var installation = Installation.Register(Guid.NewGuid(), publicIdHash, PlatformKind.Windows,
            ArchitectureKind.X64, appVersion, osVersion, InstallChannel.Stable, "1", "eu-west", seenAtUtc);
        installation.AttachDevice(deviceId, publicIdHash, seenAtUtc);
        return installation;
    }

    private static CloudDbContext CreateContext(string connectionString, bool enableRetry = false) =>
        new(new DbContextOptionsBuilder<CloudDbContext>().UseNpgsql(connectionString,
            options => { if (enableRetry) options.EnableRetryOnFailure(3); }).Options);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "PeerOnQ.Cloud.Infrastructure.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
