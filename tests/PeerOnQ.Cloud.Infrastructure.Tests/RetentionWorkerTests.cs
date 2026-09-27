using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Infrastructure.Redis;
using PeerOnQ.Cloud.Infrastructure.Workers;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class RetentionWorkerTests
{
    [Fact]
    public void PresenceHostRegistrationDoesNotEagerlyRequireDeviceAliasSecret()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=postgres;Database=peeronq;Username=presence",
            ["ConnectionStrings:Redis"] = "redis:6379",
        }).Build();
        var services = new ServiceCollection();

        var error = Record.Exception(() => services.AddPeerOnQCloudInfrastructure(
            configuration, new TestHostEnvironment()));

        Assert.Null(error);
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IDeviceRegistrationService>());
        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IPublicDeviceIdService>());
    }

    [Fact]
    public void WorkerDoesNotCaptureScopedDiagnosticStorageFromRootProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new RetentionOptions());
        services.AddSingleton(new CloudWorkerOptions());
        services.AddSingleton<IDistributedOperationLeaseManager, NoOpLeaseManager>();
        services.AddScoped<IDiagnosticBlobLifecycleStore, ScopedDiagnosticBlobStore>();
        services.AddSingleton<RetentionWorker>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(provider.GetRequiredService<RetentionWorker>());
    }

    private sealed class ScopedDiagnosticBlobStore : IDiagnosticBlobLifecycleStore
    {
        public Task DeleteAsync(Guid diagnosticId, string? storageObjectKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoOpLeaseManager : IDistributedOperationLeaseManager
    {
        public Task<IDistributedOperationLease?> TryAcquireAsync(
            string operation,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            Task.FromResult<IDistributedOperationLease?>(null);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "PeerOnQ.Presence.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
