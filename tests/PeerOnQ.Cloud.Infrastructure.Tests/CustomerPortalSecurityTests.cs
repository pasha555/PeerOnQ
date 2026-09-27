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

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class CustomerPortalSecurityTests
{
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
