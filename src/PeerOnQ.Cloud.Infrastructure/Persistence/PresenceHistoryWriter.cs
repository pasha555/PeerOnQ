using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain.Entities;

namespace PeerOnQ.Cloud.Infrastructure.Persistence;

public sealed class PresenceHistoryWriter(CloudDbContext db) : IPresenceHistoryWriter
{
    public async Task RecordEndedLeaseAsync(PresenceLease lease, DateTimeOffset endedAtUtc, CancellationToken cancellationToken)
    {
        if (endedAtUtc < lease.AcquiredAtUtc) throw new ArgumentOutOfRangeException(nameof(endedAtUtc));
        db.DevicePresenceHistory.Add(new DevicePresenceHistory(
            Guid.NewGuid(), lease.InstallationId, lease.State, lease.Region, lease.AcquiredAtUtc, endedAtUtc));
        await db.SaveChangesAsync(cancellationToken);
    }
}
