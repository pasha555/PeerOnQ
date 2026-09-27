namespace PeerOnQ.Observability.Tests;

public sealed class OperationalScriptSafetyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void PresenceWorkerLeaseKeyIsAuthorizedAndCoveredByAclSmokeTest()
    {
        var redisEntrypoint = Read("src/PeerOnQ.Infrastructure.Deployment/redis/entrypoint.sh");
        var aclSmoke = Read("src/PeerOnQ.Infrastructure.Deployment/acceptance/redis-acl-smoke.sh");

        Assert.Contains("~peeronq:cloud:v1:operation-lease:presence-expiration", redisEntrypoint, StringComparison.Ordinal);
        Assert.Contains("presence SET \"$presence_lease_key\" presence-lease-probe", aclSmoke, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSnapshotLeaseKeyIsNarrowlyAuthorizedAndCoveredByAclSmokeTest()
    {
        var redisEntrypoint = Read("src/PeerOnQ.Infrastructure.Deployment/redis/entrypoint.sh");
        var aclSmoke = Read("src/PeerOnQ.Infrastructure.Deployment/acceptance/redis-acl-smoke.sh");
        var phase6Controller = Read("scripts/windows/peeronq-phase6-dev.ps1");

        Assert.Contains("%RW~peeronq:cloud:v1:operation-lease:admin-infrastructure-snapshots", redisEntrypoint, StringComparison.Ordinal);
        Assert.Contains("admin SET \"$admin_lease_key\" admin-lease-probe", aclSmoke, StringComparison.Ordinal);
        Assert.Contains("expect_denied admin SET peeronq:cloud:v1:operation-lease:retention blocked", aclSmoke, StringComparison.Ordinal);
        Assert.Contains("Repair-StaleAdminInfrastructureLeaseAcl", phase6Controller, StringComparison.Ordinal);
        Assert.Contains("operation-lease:admin-infrastructure-snapshots", phase6Controller, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupMetricIsMadeWorldReadableBeforeAtomicPublication()
    {
        var backup = Read("src/PeerOnQ.Infrastructure.Deployment/scripts/backup-postgres.sh");
        var chmod = backup.IndexOf("chmod 644 \"$metric_partial\"", StringComparison.Ordinal);
        var publish = backup.IndexOf("mv \"$metric_partial\" \"$textfile_dir/peeronq_backup.prom\"", StringComparison.Ordinal);

        Assert.True(chmod >= 0, "The node-exporter metric must be readable outside the backup container.");
        Assert.True(publish > chmod, "The readable metric must be atomically published after permissions are set.");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PROJECT_MAP.md"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PeerOnQ repository root.");
    }
}
