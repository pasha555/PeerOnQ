using System.Text;
using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Api;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Observability;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PeerOnQ.Cloud.Infrastructure.Persistence;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class CustomerPortalSecurityTests
{
    [Theory]
    [InlineData(CustomerRegistrationMode.Closed, false)]
    [InlineData(CustomerRegistrationMode.InvitationOnly, true)]
    [InlineData(CustomerRegistrationMode.Open, true)]
    public void CapabilitiesDescribeTheConfiguredCustomerPolicy(CustomerRegistrationMode mode, bool available)
    {
        using var db = new CloudDbContext(new DbContextOptionsBuilder<CloudDbContext>().Options);
        var options = new CustomerPortalOptions { RegistrationMode = mode, Mail = new CustomerMailOptions { Provider = "FileSink" } };
        var capabilities = AccountService(db, options).GetCapabilities();
        Assert.Equal(mode.ToString(), capabilities.RegistrationMode);
        Assert.Equal(available, capabilities.RegistrationAvailable);
        Assert.True(capabilities.RequireEmailVerification);
        Assert.True(capabilities.PasswordResetAvailable);
        Assert.False(capabilities.MfaAvailable);
        Assert.Equal(new CustomerPasswordRules(12, 128, true, true, true), capabilities.PasswordRules);
    }

    [Fact]
    public async Task DisabledCustomerMfaRejectsAllMutationsBeforeAccessingStoredSecrets()
    {
        using var db = new CloudDbContext(new DbContextOptionsBuilder<CloudDbContext>().Options);
        var service = AccountService(db, DisabledMailOptions());
        var id = Guid.NewGuid();
        var setup = await Assert.ThrowsAsync<ApiProblemException>(() => service.BeginMfaSetupAsync(id, CancellationToken.None));
        var confirm = await Assert.ThrowsAsync<ApiProblemException>(() => service.ConfirmMfaAsync(id, "unused", "123456", CancellationToken.None));
        var disable = await Assert.ThrowsAsync<ApiProblemException>(() => service.DisableMfaAsync(id, "unused", "123456", CancellationToken.None));
        Assert.All(new[] { setup, confirm, disable }, error =>
        {
            Assert.Equal(403, error.StatusCode);
            Assert.Equal("customer_mfa_disabled", error.ErrorCode);
        });
        Assert.False(service.GetCapabilities().PasswordResetAvailable);
    }

    [Theory]
    [InlineData((CustomerRegistrationMode)99, true)]
    [InlineData(CustomerRegistrationMode.Open, false)]
    [InlineData(CustomerRegistrationMode.InvitationOnly, false)]
    public void ProductionRejectsUnknownModesAndUnverifiedRegistration(CustomerRegistrationMode mode, bool verify)
    {
        var result = new CustomerPortalOptionsValidator(new HostEnvironment("Production")).Validate(null, new CustomerPortalOptions
        {
            JwtSigningKey = new string('x', 64), RegistrationMode = mode, RequireEmailVerification = verify,
            Mail = new CustomerMailOptions { Provider = "Smtp", SmtpHost = "smtp.example.test" },
        });
        Assert.True(result.Failed);
    }

    private static CustomerAccountService AccountService(CloudDbContext db, CustomerPortalOptions settings)
    {
        var options = Options.Create(settings);
        return new CustomerAccountService(db, new CustomerPasswordService(), new CustomerTokenService(options),
            new EphemeralDataProtectionProvider(), new CustomerMailSender(options, new HostEnvironment("Testing")),
            options, new HttpContextAccessor());
    }

    [Fact]
    public void RequiredVerificationRejectsMissingSmtpConfigurationAtStartup()
    {
        var result = new CustomerPortalOptionsValidator(new HostEnvironment("Production")).Validate(null, new CustomerPortalOptions
        {
            JwtSigningKey = new string('x', 64), RegistrationMode = CustomerRegistrationMode.Open,
            RequireEmailVerification = true, Mail = new CustomerMailOptions { Provider = "Smtp" },
        });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("SmtpHost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SmtpConnectionFailureHasABoundedNonSecretResponse()
    {
        // A loopback-only listener supplies an unused ephemeral port, never an external mail host.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var sender = new CustomerMailSender(Options.Create(new CustomerPortalOptions
        {
            Mail = new CustomerMailOptions { Provider = "Smtp", SmtpHost = "127.0.0.1", SmtpPort = port, FromAddress = "sender@example.test" },
        }), new HostEnvironment("Testing"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ApiProblemException>(() => sender.SendAsync("person@example.test", "Example subject", "Example body", timeout.Token));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("customer_mail_unavailable", error.ErrorCode);
        Assert.DoesNotContain("127.0.0.1", error.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", error.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomerPasswordHasherUsesFrameworkFormatAndRejectsWeakPasswords()
    {
        var service = new CustomerPasswordService();
        var account = new CustomerAccount(Guid.NewGuid(), "person@example.com", "Person", "pending", DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => service.Hash(account, "short"));
        var hash = service.Hash(account, "CorrectHorseBattery9");
        account.ChangePasswordHash(hash);

        Assert.StartsWith("AQAAAA", hash, StringComparison.Ordinal);
        Assert.NotEqual(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed,
            service.Verify(account, "CorrectHorseBattery9"));
    }

    [Fact]
    public void TotpVerifierAcceptsPublishedSha1VectorAfterSixDigitReduction()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(59);

        Assert.True(CustomerTotp.Verify(secret, "287082", timestamp));
        Assert.False(CustomerTotp.Verify(secret, "287083", timestamp));
    }

    [Fact]
    public void HostPrefixedCookiesAreSecureStrictAndRootScoped()
    {
        var context = new DefaultHttpContext();
        CustomerPortalAuthentication.SetSessionCookies(context, "access", DateTimeOffset.UtcNow.AddMinutes(5),
            "refresh", DateTimeOffset.UtcNow.AddDays(1));

        var cookies = context.Response.Headers.SetCookie.ToArray();
        Assert.Equal(3, cookies.Length);
        Assert.All(cookies, value =>
        {
            Assert.Contains("secure", value!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", value!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", value!, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(cookies, value => value!.Contains("domain=", StringComparison.OrdinalIgnoreCase));
        foreach (var name in new[] { "__Host-peeronq_customer_access", "__Host-peeronq_customer_refresh" })
        {
            var cookie = Assert.Single(cookies, value => value!.StartsWith(name + "=", StringComparison.Ordinal));
            Assert.Contains("httponly", cookie!, StringComparison.OrdinalIgnoreCase);
        }
        var csrf = Assert.Single(cookies, value => value!.StartsWith("__Host-peeronq_customer_csrf=", StringComparison.Ordinal));
        Assert.DoesNotContain("httponly", csrf!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("example-csrf", null)]
    [InlineData(null, "example-csrf")]
    [InlineData("example-csrf", "different-csrf")]
    public void CustomerCsrfRejectsMissingOrMismatchedDoubleSubmitTokens(string? cookie, string? header)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null) context.Request.Headers.Cookie = $"__Host-peeronq_customer_csrf={cookie}";
        if (header is not null) context.Request.Headers["X-CSRF-Token"] = header;
        Assert.Throws<UnauthorizedAccessException>(() => CustomerPortalAuthentication.ValidateCsrf(context));
    }

    [Fact]
    public void CustomerCsrfAcceptsMatchingHostCookieAndHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "__Host-peeronq_customer_csrf=example-csrf";
        context.Request.Headers["X-CSRF-Token"] = "example-csrf";
        CustomerPortalAuthentication.ValidateCsrf(context);
    }

    [Fact]
    public void ProductionRejectsDevelopmentMailSink()
    {
        var validator = new CustomerPortalOptionsValidator(new HostEnvironment("Production"));
        var result = validator.Validate(null, new CustomerPortalOptions
        {
            JwtSigningKey = new string('x', 64),
            PortalBaseUrl = "https://portal.example.test",
            Mail = new CustomerMailOptions { Provider = "FileSink", FileSinkPath = "mail" },
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, value => value.Contains("Development or Testing", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionAcceptsDisabledMailOnlyForClosedUnverifiedPortal()
    {
        var validator = new CustomerPortalOptionsValidator(new HostEnvironment("Production"));
        var valid = validator.Validate(null, DisabledMailOptions());
        var open = validator.Validate(null, DisabledMailOptions(registrationMode: CustomerRegistrationMode.Open));
        var verified = validator.Validate(null, DisabledMailOptions(requireEmailVerification: true));

        Assert.False(valid.Failed);
        Assert.True(open.Failed);
        Assert.Contains(open.Failures!, value => value.Contains("RegistrationMode must be Closed", StringComparison.Ordinal));
        Assert.True(verified.Failed);
        Assert.Contains(verified.Failures!, value => value.Contains("RequireEmailVerification must be false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisabledMailSenderFailsBeforeCreatingExternalWork()
    {
        var sender = new CustomerMailSender(Options.Create(DisabledMailOptions()), new HostEnvironment("Production"));

        var error = await Assert.ThrowsAsync<ApiProblemException>(() =>
            sender.SendAsync("person@example.com", "subject", "body", CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
        Assert.Equal("customer_mail_disabled", error.ErrorCode);
    }

    private static CustomerPortalOptions DisabledMailOptions(
        bool requireEmailVerification = false,
        CustomerRegistrationMode registrationMode = CustomerRegistrationMode.Closed) => new()
    {
        RegistrationMode = registrationMode,
        RequireEmailVerification = requireEmailVerification,
        JwtSigningKey = new string('x', 64),
        PortalBaseUrl = "https://portal.example.test",
        Mail = new CustomerMailOptions { Provider = "Disabled" },
    };

    [Fact]
    public void CustomerRateLimitPartitionIsIsolatedByAccount()
    {
        var method = typeof(CloudApiApp).GetMethod("Fixed", BindingFlags.NonPublic | BindingFlags.Static)!;
        var first = CustomerContext(Guid.NewGuid());
        var second = CustomerContext(Guid.NewGuid());

        var firstPartition = method.Invoke(null, [first, 10, TimeSpan.FromMinutes(1)])!;
        var secondPartition = method.Invoke(null, [second, 10, TimeSpan.FromMinutes(1)])!;
        var key = firstPartition.GetType().GetProperty("PartitionKey")!;

        Assert.NotEqual(key.GetValue(firstPartition), key.GetValue(secondPartition));
    }

    private static DefaultHttpContext CustomerContext(Guid accountId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, accountId.ToString("D"))], "PeerOnQCustomer"));
        return context;
    }

    private sealed class HostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "PeerOnQ.Cloud.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
