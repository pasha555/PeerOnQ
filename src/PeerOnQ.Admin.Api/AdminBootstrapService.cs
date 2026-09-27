using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;

namespace PeerOnQ.Admin.Api;

public sealed class AdminBootstrapOptions
{
    public bool Enabled { get; set; }
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? TotpSecretBase32 { get; set; }
    public string[] RecoveryCodes { get; set; } = [];
}

public sealed class AdminBootstrapService(
    IServiceScopeFactory scopeFactory,
    IOptions<AdminBootstrapOptions> options,
    IDataProtectionProvider dataProtection,
    TimeProvider timeProvider,
    ILogger<AdminBootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (!configured.Enabled) return;
        Validate(configured);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        if (await db.AdminUsers.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Admin bootstrap was requested but an admin account already exists; no bootstrap change was made");
            return;
        }

        var now = timeProvider.GetUtcNow();
        var user = new AdminUser(Guid.NewGuid(), configured.Email!, "pending-password-hash", now);
        var passwords = scope.ServiceProvider.GetRequiredService<AdminPasswordService>();
        user.ChangePasswordHash(passwords.Hash(user, configured.Password!));
        var secret = DecodeBase32(configured.TotpSecretBase32!);
        try
        {
            user.EnableMfa(dataProtection.CreateProtector("PeerOnQ.Admin.Mfa.v1").Protect(secret));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }

        db.AdminUsers.Add(user);
        var role = await db.AdminRoles.SingleOrDefaultAsync(value => value.Name == AdminRoleKind.Owner, cancellationToken);
        if (role is null)
        {
            role = new AdminRole(Guid.NewGuid(), AdminRoleKind.Owner, requiresMfa: true);
            db.AdminRoles.Add(role);
        }
        db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, now, null));
        var privacy = scope.ServiceProvider.GetRequiredService<IPrivacyHasher>();
        db.AdminRecoveryCodes.AddRange(configured.RecoveryCodes.Select(code => new AdminRecoveryCode(
            Guid.NewGuid(), user.Id, privacy.ComputeHash("admin-recovery", NormalizeRecoveryCode(code)), now)));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Initial Owner admin account created with {EventName}; disable and remove AdminBootstrap secrets immediately",
            "admin.bootstrap.completed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void Validate(AdminBootstrapOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password) || options.Password.Length < 14)
            throw new InvalidOperationException("Admin bootstrap email and a password of at least 14 characters are required.");
        if (string.IsNullOrWhiteSpace(options.TotpSecretBase32))
            throw new InvalidOperationException("Admin bootstrap TOTP secret must decode to at least 20 bytes.");
        var decodedSecret = DecodeBase32(options.TotpSecretBase32);
        try
        {
            if (decodedSecret.Length < 20)
                throw new InvalidOperationException("Admin bootstrap TOTP secret must decode to at least 20 bytes.");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(decodedSecret);
        }
        if (options.RecoveryCodes.Length < 8 || options.RecoveryCodes.Any(code => NormalizeRecoveryCode(code).Length < 12))
            throw new InvalidOperationException("Admin bootstrap requires at least eight strong recovery codes.");
    }

    private static string NormalizeRecoveryCode(string code) => code.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var character in clean)
        {
            var index = alphabet.IndexOf(character);
            if (index < 0) throw new InvalidOperationException("Admin bootstrap TOTP secret is not valid Base32.");
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }
        return output.ToArray();
    }
}
