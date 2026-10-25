using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain.Entities;

namespace PeerOnQ.Admin.Api;

public sealed class AdminPasswordService
{
    private readonly PasswordHasher<AdminUser> _hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = 210_000,
    }));
    private readonly AdminUser _dummyUser;
    private readonly string _dummyHash;

    public AdminPasswordService(TimeProvider timeProvider)
    {
        _dummyUser = new AdminUser(Guid.NewGuid(), "unknown@invalid.invalid", "initial", timeProvider.GetUtcNow());
        _dummyHash = _hasher.HashPassword(_dummyUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public PasswordVerificationResult Verify(AdminUser? user, string suppliedPassword) =>
        user is null
            ? _hasher.VerifyHashedPassword(_dummyUser, _dummyHash, suppliedPassword)
            : _hasher.VerifyHashedPassword(user, user.PasswordHash, suppliedPassword);

    public string Hash(AdminUser user, string password) => _hasher.HashPassword(user, password);
}
