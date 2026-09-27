using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain.Entities;

namespace PeerOnQ.Cloud.Infrastructure.Persistence;

public sealed class DeviceBootstrapStore(
    IDbContextFactory<CloudDbContext> contextFactory,
    IPublicDeviceIdService publicDeviceIds) : IDeviceBootstrapStore
{
    private const int MaximumAliasCandidates = 4096;

    public async Task<DeviceBootstrapResult> CompleteAsync(
        DeviceBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        for (var collisionCounter = 0; collisionCounter < MaximumAliasCandidates; collisionCounter++)
        {
            try
            {
                await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
                var strategy = strategyContext.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
                    await using var transaction = await db.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable, cancellationToken);

                    var device = await db.Devices.SingleOrDefaultAsync(
                        value => value.IdentityFingerprint == request.IdentityFingerprint,
                        cancellationToken);
                    var deviceCreated = device is null;
                    ServerAssignedPublicDeviceId alias;

                    if (device is null)
                    {
                        alias = publicDeviceIds.DeriveFromFingerprint(
                            request.IdentityFingerprint, collisionCounter);
                        if (await db.Devices.AnyAsync(
                                value => value.PublicDeviceIdHash == alias.LookupHash,
                                cancellationToken))
                        {
                            throw new PublicAliasCollisionException();
                        }

                        device = Device.Create(
                            alias.LookupHash,
                            alias.MaskedValue,
                            request.DisplayName,
                            request.IdentityFingerprint,
                            alias.CollisionCounter,
                            alias.KeyVersion,
                            request.AuthenticatedAtUtc);
                        db.Devices.Add(device);
                    }
                    else
                    {
                        if (device.IsRevoked)
                            throw new CloudServiceException(CloudErrorCodes.DeviceRevoked,
                                "The device identity is revoked.");

                        if (device.PublicDeviceIdCollisionCounter < 0 ||
                            device.PublicDeviceIdKeyVersion <= 0)
                        {
                            alias = publicDeviceIds.DeriveFromFingerprint(
                                request.IdentityFingerprint, collisionCounter);
                            if (await db.Devices.AnyAsync(
                                    value => value.Id != device.Id &&
                                             value.PublicDeviceIdHash == alias.LookupHash,
                                    cancellationToken))
                            {
                                throw new PublicAliasCollisionException();
                            }
                            device.AssignServerAlias(
                                alias.LookupHash,
                                alias.MaskedValue,
                                alias.CollisionCounter,
                                alias.KeyVersion,
                                request.AuthenticatedAtUtc);
                        }
                        else
                        {
                            alias = publicDeviceIds.DeriveFromFingerprint(
                                request.IdentityFingerprint,
                                device.PublicDeviceIdCollisionCounter,
                                device.PublicDeviceIdKeyVersion);
                            if (!CryptographicOperations.FixedTimeEquals(
                                    device.PublicDeviceIdHash, alias.LookupHash))
                            {
                                throw new InvalidOperationException(
                                    "Stored public alias binding does not match its derivation key and counter.");
                            }
                        }
                        device.Touch(request.DisplayName, request.AuthenticatedAtUtc);
                    }

                    var installation = await db.Installations.SingleOrDefaultAsync(
                        value => value.Id == request.InstallationId,
                        cancellationToken);
                    var installationCreated = installation is null;
                    if (installation is null)
                    {
                        installation = Installation.RegisterProofBound(
                            request.InstallationId,
                            device.Id,
                            alias.LookupHash,
                            request.Platform,
                            request.Architecture,
                            request.AppVersion,
                            request.OsVersion,
                            request.InstallChannel,
                            request.ProtocolVersion,
                            request.Region,
                            request.AuthenticatedAtUtc);
                        db.Installations.Add(installation);
                    }
                    else
                    {
                        try
                        {
                            installation.ConfirmProofBinding(
                                device.Id,
                                alias.LookupHash,
                                request.Platform,
                                request.Architecture,
                                request.AppVersion,
                                request.OsVersion,
                                request.InstallChannel,
                                request.ProtocolVersion,
                                request.Region,
                                request.AuthenticatedAtUtc);
                        }
                        catch (InvalidOperationException exception)
                        {
                            throw new CloudServiceException(
                                CloudErrorCodes.InstallationIdentityConflict,
                                "The installation is proof-bound to another device identity.",
                                isPermanent: true,
                                exception);
                        }
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    ManagedDevicePolicyAttestation? managedPolicy = null;
                    if (device.OwnerOrganizationId is Guid organizationId)
                    {
                        var policy = await db.OrganizationPolicies.AsNoTracking().SingleAsync(
                            value => value.OrganizationId == organizationId, cancellationToken);
                        managedPolicy = new ManagedDevicePolicyAttestation(
                            organizationId,
                            policy.ViewOnlyAllowed,
                            policy.FullControlAllowed,
                            policy.FileTransferAllowed,
                            policy.UnattendedAccessAllowed,
                            policy.ClipboardAllowed,
                            policy.HybridSecurityRequired,
                            policy.MinimumClientVersion,
                            policy.ApprovedRelayRegionsCsv);
                    }
                    await transaction.CommitAsync(cancellationToken);
                    return new DeviceBootstrapResult(
                        device.Id,
                        installation.Id,
                        alias.Value,
                        deviceCreated,
                        installationCreated,
                        managedPolicy);
                });
            }
            catch (PublicAliasCollisionException)
            {
                // Deterministic next counter; no random or client-selected alias fallback.
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "IX_Devices_PublicDeviceIdHash"
                })
            {
                // A concurrent fingerprint won this alias; retry with the next deterministic counter.
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation
                })
            {
                // Fingerprint/installation races are re-read in a fresh serializable transaction.
                if (collisionCounter + 1 >= MaximumAliasCandidates) throw;
            }
        }

        throw new InvalidOperationException("No unique public device alias was available.");
    }

    private sealed class PublicAliasCollisionException : Exception;
}
