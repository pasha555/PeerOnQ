using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PeerOnQ.Cloud.Api;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class DiagnosticStorageOptionsValidatorTests
{
    [Fact]
    public void Production_file_system_storage_requires_explicit_single_node_opt_in()
    {
        var validator = new DiagnosticStorageOptionsValidator(new TestHostEnvironment("Production"));

        var result = validator.Validate(null, new DiagnosticStorageOptions
        {
            Provider = "FileSystem",
            FileSystemPath = "/var/lib/peeronq/diagnostics",
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Production_file_system_storage_accepts_explicit_single_node_opt_in()
    {
        var validator = new DiagnosticStorageOptionsValidator(new TestHostEnvironment("Production"));

        var result = validator.Validate(null, new DiagnosticStorageOptions
        {
            Provider = "FileSystem",
            FileSystemPath = "/var/lib/peeronq/diagnostics",
            AllowFileSystemOutsideDevelopment = true,
        });

        Assert.True(result.Succeeded);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "PeerOnQ.Tests";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
