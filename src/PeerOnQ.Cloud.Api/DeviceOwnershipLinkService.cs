using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Cloud.Infrastructure.Redis;
using PeerOnQ.Observability;
using PeerOnQ.Shared.Contracts.V1;
using StackExchange.Redis;

namespace PeerOnQ.Cloud.Api;

public sealed class DeviceOwnershipLinkService(
    CloudDbContext db,
    IConnectionMultiplexer redis,
    RedisKeySpace keySpace,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private const string ReplaceIfCurrent = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl <= 0 then return 0 end
        redis.call('SET', KEYS[1], ARGV[2], 'PX', ttl)
        return 1
        """;
    private const string ConsumeIfCurrent = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
        return redis.call('DEL', KEYS[1])
        """;

    public async Task<CreateDeviceOwnershipLinkResponse> CreateAsync(DeviceAccessPrincipal principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        keySpace.Validate();
        var now = timeProvider.GetUtcNow();
        if (principal.ExpiresAtUtc <= now) throw Invalid();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(value => value.Id == principal.DeviceId, cancellationToken)
            ?? throw Invalid();
        var installation = await db.Installations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == principal.InstallationId, cancellationToken)
            ?? throw Invalid();
        EnsureDeviceActive(device, installation);
        EnsurePrincipalBinding(principal, device, installation);
        var code = CustomerTokenService.IssueOpaqueToken();
        var expires = now + Lifetime;
        var pending = new PendingLink(device.Id, installation.Id, device.IdentityVersion, device.IdentityFingerprint,
            installation.ProofBindingVersion, expires, null, null, null, null);
        if (!await redis.GetDatabase().StringSetAsync(Key(code), JsonSerializer.Serialize(pending), Lifetime, When.NotExists))
            throw new InvalidOperationException("Could not create a unique ownership challenge.");
        return new(code, expires);
    }

    public async Task<DeviceOwnershipLinkStatusResponse> StatusAsync(DeviceAccessPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.ExpiresAtUtc <= timeProvider.GetUtcNow()) throw Invalid();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(value => value.Id == principal.DeviceId, cancellationToken) ?? throw Invalid();
        var installation = await db.Installations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == principal.InstallationId, cancellationToken) ?? throw Invalid();
        EnsureDeviceActive(device, installation);
        EnsurePrincipalBinding(principal, device, installation);
        return new(device.OwnerOrganizationId is not null);
    }

    public async Task<PreviewDeviceOwnershipLinkResponse> PreviewAsync(Guid accountId, Guid sessionId, Guid organizationId, string code, CancellationToken cancellationToken)
    {
        await RequireActorAsync(accountId, sessionId, organizationId, cancellationToken);
        var (key, raw, pending) = await ReadAsync(code, cancellationToken);
        EnsureActorBinding(pending, accountId, sessionId, organizationId);
        var (device, _) = await ReadBoundDeviceAsync(pending, organizationId, cancellationToken);
        var preview = CustomerTokenService.IssueOpaqueToken();
        var bound = pending with { AccountId = accountId, SessionId = sessionId, OrganizationId = organizationId, PreviewHash = Hash(preview) };
        if ((int)await redis.GetDatabase().ScriptEvaluateAsync(ReplaceIfCurrent, [key], [raw, JsonSerializer.Serialize(bound)]) != 1)
            throw Invalid();
        return new(device.Id, device.MaskedPublicDeviceId, device.DisplayName, pending.ExpiresAtUtc, preview);
    }

    public async Task ClaimAsync(Guid accountId, Guid sessionId, Guid organizationId, string code, string previewToken, CancellationToken cancellationToken)
    {
        await RequireActorAsync(accountId, sessionId, organizationId, cancellationToken);
        var (key, raw, pending) = await ReadAsync(code, cancellationToken);
        EnsureActorBinding(pending, accountId, sessionId, organizationId);
        if (pending.AccountId != accountId || pending.PreviewHash is null || !ValidToken(previewToken) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pending.PreviewHash), Encoding.ASCII.GetBytes(Hash(previewToken))))
            throw Invalid();
        var (device, installation) = await ReadBoundDeviceAsync(pending, organizationId, cancellationToken);
        if ((int)await redis.GetDatabase().ScriptEvaluateAsync(ConsumeIfCurrent, [key], [raw]) != 1)
            throw Invalid();
        // Consuming before the database write is intentionally fail-closed: a failed write
        // requires a fresh native challenge and never leaves a replayable ownership grant.
        db.Attach(device);
        db.Attach(installation);
        installation.ConfirmOwnershipLink();
        if (device.OwnerOrganizationId is null)
            device.AssignOwner(accountId, organizationId, timeProvider.GetUtcNow());
        db.CustomerSecurityEvents.Add(new CustomerSecurityEvent(Guid.NewGuid(), accountId, organizationId,
            "organization.device_claimed", AuditResult.Succeeded, timeProvider.GetUtcNow(),
            httpContextAccessor.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N"), null));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "ownership_changed", "The device state changed. Generate a new ownership link and try again.");
        }
    }

    private async Task RequireActorAsync(Guid accountId, Guid sessionId, Guid organizationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!await db.CustomerAccounts.AsNoTracking().AnyAsync(value => value.Id == accountId && value.Status == CustomerAccountStatus.Active, cancellationToken) ||
            !await db.CustomerSessions.AsNoTracking().AnyAsync(value => value.Id == sessionId && value.AccountId == accountId && value.RevokedAtUtc == null && value.ExpiresAtUtc > now, cancellationToken) ||
            !await db.Organizations.AsNoTracking().AnyAsync(value => value.Id == organizationId && value.DeletionRequestedAtUtc == null, cancellationToken) ||
            !await db.OrganizationMemberships.AsNoTracking().AnyAsync(value => value.OrganizationId == organizationId && value.AccountId == accountId &&
                value.RevokedAtUtc == null && (value.Role == CustomerRoleKind.Owner || value.Role == CustomerRoleKind.Administrator), cancellationToken))
            throw new UnauthorizedAccessException();
    }

    private async Task<(RedisKey Key, RedisValue Raw, PendingLink Pending)> ReadAsync(string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidToken(code)) throw Invalid();
        var key = Key(code);
        var raw = await redis.GetDatabase().StringGetAsync(key);
        if (raw.IsNullOrEmpty) throw Invalid();
        var pending = JsonSerializer.Deserialize<PendingLink>((string)raw!);
        if (pending is null || pending.ExpiresAtUtc <= timeProvider.GetUtcNow()) throw Invalid();
        return (key, raw, pending);
    }

    private async Task<(Device Device, Installation Installation)> ReadBoundDeviceAsync(PendingLink pending, Guid organizationId, CancellationToken cancellationToken)
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(value => value.Id == pending.DeviceId, cancellationToken) ?? throw Invalid();
        var installation = await db.Installations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == pending.InstallationId, cancellationToken) ?? throw Invalid();
        EnsureDeviceActive(device, installation);
        if (device.IdentityVersion != pending.IdentityVersion || device.IdentityFingerprint != pending.IdentityFingerprint || installation.ProofBindingVersion != pending.ProofBindingVersion)
            throw Invalid();
        if ((device.OwnerOrganizationId is not null && device.OwnerOrganizationId != organizationId) ||
            (device.OwnerOrganizationId is null && device.OwnerUserId is not null))
            throw new ApiProblemException(StatusCodes.Status409Conflict, "device_already_owned", "This device is already assigned to another owner.");
        return (device, installation);
    }

    private static void EnsureDeviceActive(Device device, Installation installation)
    {
        if (device.IsRevoked || installation.DeviceId != device.Id || installation.ProofBindingVersion <= 0 || installation.IsBlocked || installation.UnregisteredAtUtc is not null)
            throw Invalid();
    }

    private static void EnsurePrincipalBinding(DeviceAccessPrincipal principal, Device device, Installation installation)
    {
        if (principal.IdentityVersion <= 0 || principal.IdentityVersion != device.IdentityVersion ||
            principal.ProofBindingVersion <= 0 || principal.ProofBindingVersion != installation.ProofBindingVersion)
            throw Invalid();
    }

    private static void EnsureActorBinding(PendingLink pending, Guid accountId, Guid sessionId, Guid organizationId)
    {
        if (pending.AccountId is not null && (pending.AccountId != accountId || pending.SessionId != sessionId || pending.OrganizationId != organizationId))
            throw Invalid();
    }

    private RedisKey Key(string code) => $"{keySpace.Prefix}:ownership-link:{Hash(code)}";
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    private static bool ValidToken(string? token) => token is { Length: 43 } && token.All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_');
    private static ApiProblemException Invalid() => new(StatusCodes.Status400BadRequest, "ownership_link_invalid", "The ownership link is invalid, expired, already used, or bound to another account, session, or organization.");
    private sealed record PendingLink(Guid DeviceId, Guid InstallationId, int IdentityVersion, string IdentityFingerprint,
        int ProofBindingVersion, DateTimeOffset ExpiresAtUtc, Guid? AccountId, Guid? SessionId, Guid? OrganizationId, string? PreviewHash);
}

public sealed record PreviewDeviceOwnershipLinkRequest(string Code);
public sealed record PreviewDeviceOwnershipLinkResponse(Guid DeviceId, string MaskedPublicDeviceId, string DisplayName, DateTimeOffset ExpiresAtUtc, string PreviewToken);
public sealed record ClaimDeviceOwnershipLinkRequest(string Code, string PreviewToken);
