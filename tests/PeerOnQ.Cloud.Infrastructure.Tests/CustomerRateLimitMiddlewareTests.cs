using System.Net;
using System.Reflection;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Api;
using PeerOnQ.Cloud.Domain.Entities;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class CustomerRateLimitMiddlewareTests
{
    [Fact]
    public async Task RealCustomerCookiesHaveIndependentBudgetsBehindOneIpAndAnonymousIpLimitsRemain()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"peeronq-rate-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = directory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PeerOnQ:CustomerPortal:JwtSigningKey"] = new string('x', 64),
            ["PeerOnQ:CustomerPortal:DataProtectionPath"] = Path.Combine(directory, "keys"),
        });
        builder.Services.AddAuthentication("PeerOnQDevice")
            .AddScheme<AuthenticationSchemeOptions, NoDeviceCookieHandler>("PeerOnQDevice", _ => { });
        builder.Services.AddCustomerPortalAuthentication(builder.Configuration, builder.Environment);
        // Session database validation has separate persistence tests. Keep actual JWT signature,
        // issuer, audience, lifetime, cookie handling and claim mapping for this middleware test.
        builder.Services.PostConfigure<JwtBearerOptions>(CustomerPortalAuthentication.Scheme,
            options => options.Events.OnTokenValidated = _ => Task.CompletedTask);
        foreach (var method in new[] { "AddAuthenticationRouting", "AddRateLimiting" })
            typeof(CloudApiApp).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [builder.Services]);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapPost("/portal/v1/sensitive", () => Results.Ok()).RequireAuthorization(CustomerPortalAuthentication.Policy).RequireRateLimiting("customer-sensitive");
        app.MapPost("/portal/v1/anonymous", () => Results.Ok()).AllowAnonymous().RequireRateLimiting("customer-registration");
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var settings = builder.Configuration.GetSection(CustomerPortalOptions.SectionName).Get<CustomerPortalOptions>()!;
            var tokens = new CustomerTokenService(Options.Create(settings));
            var first = Token(tokens);
            var second = Token(tokens);
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
            for (var index = 0; index < 30; index++)
                Assert.Equal(HttpStatusCode.OK, await SendAsync(client, address + "/portal/v1/sensitive", first));
            Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(client, address + "/portal/v1/sensitive", first));
            Assert.Equal(HttpStatusCode.OK, await SendAsync(client, address + "/portal/v1/sensitive", second));
            for (var index = 0; index < 5; index++)
                Assert.Equal(HttpStatusCode.OK, await SendAsync(client, address + "/portal/v1/anonymous", null));
            Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(client, address + "/portal/v1/anonymous", null));
        }
        finally
        {
            await app.StopAsync();
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static string Token(CustomerTokenService tokens)
    {
        var now = DateTimeOffset.UtcNow;
        var account = new CustomerAccount(Guid.NewGuid(), $"{Guid.NewGuid():N}@example.test", "Test", "pending", now);
        var session = new CustomerSession(Guid.NewGuid(), account.Id, Guid.NewGuid(), new byte[32], now, now.AddHours(1), "Test");
        return tokens.IssueAccessToken(account, session, now).Token;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string url, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null) request.Headers.Add("Cookie", $"{CustomerPortalAuthentication.AccessCookie}={token}");
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    public sealed class NoDeviceCookieHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
