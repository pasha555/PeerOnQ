using System.Buffers.Binary;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PeerOnQ.Admin.Api;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Observability;

namespace PeerOnQ.Admin.Api.Tests;

public sealed class AdminSecurityTests
{
    [Fact]
    public void Totp_AcceptsOnlyCurrentOrAdjacentWindow()
    {
        var secret = Convert.FromHexString("3132333435363738393031323334353637383930");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_234_567_890);
        var code = GenerateTotp(secret, now.ToUnixTimeSeconds() / 30);

        Assert.True(TotpVerifier.Verify(secret, code, now));
        Assert.True(TotpVerifier.Verify(secret, code, now.AddSeconds(30)));
        Assert.False(TotpVerifier.Verify(secret, code, now.AddMinutes(2)));
        Assert.False(TotpVerifier.Verify(secret, "12345a", now));
    }

    [Fact]
    public void TokenService_IssuesShortLivedRoleAndMfaClaimsAndRandomRefreshTokens()
    {
        var now = DateTimeOffset.Parse("2026-08-11T10:00:00Z");
        var time = new MutableTimeProvider(now);
        var service = new AdminTokenService(Options.Create(ValidOptions()), time);
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var issued = service.IssueAccessToken(userId, sessionId, new HashSet<AdminRoleKind> { AdminRoleKind.Owner }, true);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.Token);
        var refreshOne = service.CreateRefreshToken();
        var refreshTwo = service.CreateRefreshToken();

        Assert.Equal(now.AddMinutes(5), issued.ExpiresAtUtc);
        Assert.Contains(jwt.Claims, claim => claim.Type == JwtRegisteredClaimNames.Sub && claim.Value == userId.ToString("N"));
        Assert.Contains(jwt.Claims, claim => claim.Type == JwtRegisteredClaimNames.Sid && claim.Value == sessionId.ToString("N"));
        Assert.Contains(jwt.Claims, claim => claim.Type == "mfa" && claim.Value == "true");
        Assert.Contains(jwt.Claims, claim => claim.Type == System.Security.Claims.ClaimTypes.Role && claim.Value == nameof(AdminRoleKind.Owner));
        Assert.NotEqual(refreshOne, refreshTwo);
        Assert.NotEqual(service.HashRefreshToken(refreshOne), service.HashRefreshToken(refreshTwo));
    }

    [Fact]
    public async Task TokenValidation_FailsImmediatelyWhenBoundSessionIsRevoked()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var services = new ServiceCollection()
            .AddSingleton<IAdminSessionAccessValidator>(new StubSessionValidator(active: false))
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sub, userId.ToString("N")),
            new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sid, sessionId.ToString("N")),
        ], JwtBearerDefaults.AuthenticationScheme));
        var context = new TokenValidatedContext(
            http,
            new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Principal = principal,
        };

        await AdminApiApp.ValidateAdminSessionAsync(context);

        Assert.NotNull(context.Result?.Failure);
    }

    [Fact]
    public void AuthenticationOptions_FailClosedOnWeakOrNonHttpsConfiguration()
    {
        var validator = new AdminAuthenticationOptionsValidator();
        var valid = ValidOptions();

        Assert.True(validator.Validate(null, valid).Succeeded);
        valid.Issuer = "http://admin.peeronq.test";
        valid.SigningKey = "too-short";
        Assert.True(validator.Validate(null, valid).Failed);
    }

    [Fact]
    public void MfaBypass_RequiresExplicitDevelopmentEnvironment()
    {
        var options = ValidOptions();
        var development = new TestEnvironment(Environments.Development);
        var production = new TestEnvironment(Environments.Production);

        AdminAuthenticationConfiguration.EnsureMfaBypassAllowed(production, options);
        Assert.False(AdminAuthenticationConfiguration.IsMfaBypassEnabled(development, options));

        options.AllowMfaBypassForDevelopment = true;
        AdminAuthenticationConfiguration.EnsureMfaBypassAllowed(development, options);
        Assert.True(AdminAuthenticationConfiguration.IsMfaBypassEnabled(development, options));
        Assert.False(AdminAuthenticationConfiguration.IsMfaBypassEnabled(production, options));
        Assert.Throws<InvalidOperationException>(() =>
            AdminAuthenticationConfiguration.EnsureMfaBypassAllowed(production, options));
    }

    [Fact]
    public async Task PlatformUpgradePolicy_RequiresMfaAndOwnerWhileReleaseManagerCanOnlyStage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AdminApiApp.AddAdminAuthorizationPolicies(services.AddAuthorizationBuilder());
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var ownerWithMfa = AdminPrincipal(AdminRoleKind.Owner, mfa: true);
        var ownerWithoutMfa = AdminPrincipal(AdminRoleKind.Owner, mfa: false);
        var releaseManager = AdminPrincipal(AdminRoleKind.ReleaseManager, mfa: true);

        Assert.True((await authorization.AuthorizeAsync(
            ownerWithMfa, null, "admin.platform-upgrade")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(
            ownerWithoutMfa, null, "admin.platform-upgrade")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(
            releaseManager, null, "admin.platform-upgrade")).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(
            releaseManager, null, "admin.release")).Succeeded);
    }

    [Fact]
    public void AdminKestrelCeiling_AllowsTheScopedPlatformUploadRoute()
    {
        var options = new KestrelServerOptions();

        AdminApiApp.ConfigureAdminKestrelRequestLimit(options);

        Assert.Equal(PlatformUpgradeOptions.MaximumRequestBodyBytes, options.Limits.MaxRequestBodySize);
    }

    [Fact]
    public void AdminQueries_EnforcePaginationDateAndSortBounds()
    {
        var valid = new AdminQueryParameters { Offset = 0, Limit = 200, SortBy = "createdAt" };
        Assert.Equal(200, valid.ToContract("createdAt").Limit);

        Assert.Throws<ArgumentException>(() => new AdminQueryParameters { Limit = 201 }.ToContract("createdAt"));
        Assert.Throws<ArgumentException>(() => new AdminQueryParameters { Limit = 10, SortBy = "secret" }.ToContract("createdAt"));
        Assert.Throws<ArgumentException>(() => new AdminQueryParameters
        {
            Limit = 10,
            FromUtc = DateTimeOffset.UtcNow,
            ToUtc = DateTimeOffset.UtcNow.AddDays(367),
        }.ToContract("createdAt"));
    }

    [Fact]
    public void AlertIngestion_RequiresStrongServiceAuthenticationAndAllowlistedRunbooks()
    {
        var configured = new AlertIngestionOptions
        {
            Token = new string('a', 64),
            AllowedRunbookHosts = ["docs.peeronq.test"],
            DefaultRegion = "eu-central",
            MaximumAlertsPerWebhook = 100,
        };
        var validator = new AlertIngestionOptionsValidator();
        var tokens = new AlertIngestionTokenProvider(Options.Create(configured));

        Assert.True(validator.Validate(null, configured).Succeeded);
        Assert.True(tokens.Validate(new string('a', 64)));
        Assert.False(tokens.Validate(new string('b', 64)));

        configured.TokenFile = "/run/secrets/duplicate";
        configured.AllowedRunbookHosts = [];
        Assert.True(validator.Validate(null, configured).Failed);
    }

    [Fact]
    public void AlertIngestion_AllowsBoundedRunbookFragmentsWithoutWeakeningUrlTrustChecks()
    {
        string[] allowedHosts = ["docs.peeronq.test"];
        var oversizedFragment = new string('a', AlertmanagerIngestion.MaximumRunbookFragmentLength + 1);

        Assert.True(AlertmanagerIngestion.IsAllowedRunbook(
            new Uri("https://docs.peeronq.test/runbooks/phase6#api-high-error-rate"),
            allowedHosts));
        Assert.False(AlertmanagerIngestion.IsAllowedRunbook(
            new Uri($"https://docs.peeronq.test/runbooks/phase6#{oversizedFragment}"),
            allowedHosts));
        Assert.False(AlertmanagerIngestion.IsAllowedRunbook(
            new Uri("http://docs.peeronq.test/runbooks/phase6#api-high-error-rate"),
            allowedHosts));
        Assert.False(AlertmanagerIngestion.IsAllowedRunbook(
            new Uri("https://operator@docs.peeronq.test/runbooks/phase6#api-high-error-rate"),
            allowedHosts));
        Assert.False(AlertmanagerIngestion.IsAllowedRunbook(
            new Uri("https://docs.peeronq.test.evil.test/runbooks/phase6#api-high-error-rate"),
            allowedHosts));
    }

    [Fact]
    public void InfrastructureMetrics_RejectUnallowlistedOrInsecureProductionEndpoint()
    {
        var validator = new AdminInfrastructureMetricsOptionsValidator(new TestEnvironment(Environments.Production));
        var options = new AdminInfrastructureMetricsOptions
        {
            PrometheusEndpoint = new Uri("https://prometheus.peeronq.test/"),
            AllowedHosts = ["prometheus.peeronq.test"],
        };
        Assert.True(validator.Validate(null, options).Succeeded);

        options.PrometheusEndpoint = new Uri("http://prometheus.peeronq.test/");
        Assert.True(validator.Validate(null, options).Failed);
    }

    [Fact]
    public void DataProtection_RequiresProtectorOutsideExplicitDevelopmentOptIn()
    {
        var options = new AdminDataProtectionOptions { AllowUnprotectedKeysForDevelopment = true };

        AdminDataProtectionConfiguration.EnsureUnprotectedModeAllowed(
            new TestEnvironment(Environments.Development), options);
        Assert.Throws<InvalidOperationException>(() =>
            AdminDataProtectionConfiguration.EnsureUnprotectedModeAllowed(
                new TestEnvironment(Environments.Production), options));
    }

    [Fact]
    public void DataProtection_LoadsPasswordProtectedCertificateWithPrivateKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"peeronq-admin-dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string password = "correct-horse-battery-staple"; // secret-scan: allow-test-vector
            var certificatePath = Path.Combine(directory, "data-protection.pfx");
            var passwordPath = Path.Combine(directory, "password");
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=PeerOnQ Admin Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var source = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllBytes(certificatePath, source.Export(X509ContentType.Pfx, password));
            File.WriteAllText(passwordPath, password + Environment.NewLine);

            using var loaded = AdminDataProtectionConfiguration.LoadCertificate(new AdminDataProtectionOptions
            {
                CertificatePath = certificatePath,
                CertificatePasswordFile = passwordPath,
            });

            Assert.True(loaded.HasPrivateKey);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AdminMutationCsrf_RequiresMatchingCookieAndHeader()
    {
        var missing = new DefaultHttpContext();
        Assert.Throws<UnauthorizedAccessException>(() => AdminAuthService.ValidateCsrf(missing));

        var mismatch = new DefaultHttpContext();
        mismatch.Request.Headers.Cookie = $"{AdminAuthService.CsrfCookie}=cookie-value";
        mismatch.Request.Headers[AdminAuthService.CsrfHeader] = "header-value";
        Assert.Throws<UnauthorizedAccessException>(() => AdminAuthService.ValidateCsrf(mismatch));

        var valid = new DefaultHttpContext();
        valid.Request.Headers.Cookie = $"{AdminAuthService.CsrfCookie}=same-random-value";
        valid.Request.Headers[AdminAuthService.CsrfHeader] = "same-random-value";
        AdminAuthService.ValidateCsrf(valid);
    }

    [Fact]
    public void ReleasePublication_VerifiesOfflineSignatureAndImmutablePackageMetadata()
    {
        var now = DateTimeOffset.Parse("2026-08-11T10:00:00Z");
        var signed = CreateSignedRelease(now, "https://updates.peeronq.test/stable/x64/PeerOnQ-1.2.3-x64.msi");
        var verifier = new ReleasePublicationVerifier(Options.Create(signed.Options), new MutableTimeProvider(now));

        var release = verifier.Verify(signed.Envelope);

        Assert.Equal("1.2.3.0", release.Version);
        Assert.Equal(InstallChannel.Stable, release.Channel);
        Assert.Equal(ArchitectureKind.X64, release.Architecture);
        Assert.Equal(25, release.RolloutPercentage);
        Assert.Equal(new string('a', 64), release.ArtifactSha256);
    }

    [Fact]
    public void ReleasePublication_RejectsASignedPackageOutsideTheConfiguredOrigin()
    {
        var now = DateTimeOffset.Parse("2026-08-11T10:00:00Z");
        var signed = CreateSignedRelease(now, "https://attacker.example/stable/x64/PeerOnQ-1.2.3-x64.msi");
        var verifier = new ReleasePublicationVerifier(Options.Create(signed.Options), new MutableTimeProvider(now));

        var error = Assert.Throws<ApiProblemException>(() => verifier.Verify(signed.Envelope));

        Assert.Equal("release_artifact_url_invalid", error.ErrorCode);
    }

    [Fact]
    public void ReleasePublication_RejectsTamperingAfterSigning()
    {
        var now = DateTimeOffset.Parse("2026-08-11T10:00:00Z");
        var signed = CreateSignedRelease(now, "https://updates.peeronq.test/stable/x64/PeerOnQ-1.2.3-x64.msi");
        var envelope = JsonSerializer.Deserialize<Dictionary<string, object>>(signed.Envelope)!;
        var payload = Convert.FromBase64String(envelope["payload"].ToString()!);
        payload[^2] ^= 1;
        envelope["payload"] = Convert.ToBase64String(payload);
        var tampered = JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var verifier = new ReleasePublicationVerifier(Options.Create(signed.Options), new MutableTimeProvider(now));

        var error = Assert.Throws<ApiProblemException>(() => verifier.Verify(tampered));

        Assert.Equal("release_signature_invalid", error.ErrorCode);
    }

    private static AdminAuthenticationOptions ValidOptions() => new()
    {
        Issuer = "https://admin.peeronq.test",
        Audience = "peeronq-admin",
        SigningKey = new string('s', 64),
        RefreshHashKey = new string('r', 64),
        AccessTokenMinutes = 5,
        RefreshTokenHours = 8,
        MfaChallengeMinutes = 5,
        MaxFailedAttempts = 5,
        LockoutMinutes = 15,
    };

    private static System.Security.Claims.ClaimsPrincipal AdminPrincipal(AdminRoleKind role, bool mfa) =>
        new(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim("mfa", mfa ? "true" : "false"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role.ToString()),
        ], "test"));

    private static string GenerateTotp(byte[] secret, long counter)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, counter);
        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(bytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (byte[] Envelope, ReleasePublicationOptions Options) CreateSignedRelease(
        DateTimeOffset now,
        string packageUrl)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            productId = "com.peeronq.desktop",
            version = "1.2.3.0",
            minimumSupportedVersion = "1.0.0.0",
            channel = 0,
            issuedAt = now.AddMinutes(-1),
            expiresAt = now.AddDays(2),
            rolloutPercentage = 25,
            rolloutSeed = "release-test-seed",
            securityEmergency = false,
            packages = new[]
            {
                new
                {
                    architecture = "x64",
                    url = packageUrl,
                    sha256 = new string('a', 64),
                    sizeBytes = 123L,
                    installerType = "msi",
                },
            },
        }, json);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            keyId = "release-key-1",
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(signature),
        }, json);
        return (envelope, new ReleasePublicationOptions
        {
            Enabled = true,
            StorageDirectory = Path.Combine(Path.GetTempPath(), "peeronq-release-test"),
            PublicBaseUrl = new Uri("https://updates.peeronq.test/"),
            KeyId = "release-key-1",
            PublicKeySpkiBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            MaximumPackageBytes = 1024,
        });
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubSessionValidator(bool active) : IAdminSessionAccessValidator
    {
        public Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(active);
    }
}
