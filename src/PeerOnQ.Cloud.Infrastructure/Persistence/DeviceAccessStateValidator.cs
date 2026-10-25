using Microsoft.EntityFrameworkCore;
using PeerOnQ.Cloud.Application.Abstractions;

namespace PeerOnQ.Cloud.Infrastructure.Persistence;

public sealed class DeviceAccessStateValidator(CloudDbContext db) : IDeviceAccessStateValidator
{
    public Task<bool> IsActiveAsync(DeviceAccessPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.DeviceId == Guid.Empty || principal.InstallationId == Guid.Empty)
            return Task.FromResult(false);

        return db.Installations
            .AsNoTracking()
            .Where(installation =>
                installation.Id == principal.InstallationId
                && installation.DeviceId == principal.DeviceId
                && (principal.ProofBindingVersion == 0 || installation.ProofBindingVersion == principal.ProofBindingVersion)
                && !installation.IsBlocked
                && installation.UnregisteredAtUtc == null)
            .Join(
                db.Devices.AsNoTracking().Where(device => !device.IsRevoked && (principal.IdentityVersion == 0 || device.IdentityVersion == principal.IdentityVersion)),
                installation => installation.DeviceId,
                device => (Guid?)device.Id,
                (_, _) => true)
            .AnyAsync(cancellationToken);
    }
}
