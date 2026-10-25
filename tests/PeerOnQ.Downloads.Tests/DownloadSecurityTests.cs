using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Downloads.Service;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Downloads.Tests;

public sealed class DownloadSecurityTests
{
    [Fact]
    public void CompletionTokens_AreBoundToDownloadAndExpire()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-11T10:00:00Z"));
        var service = new DownloadCompletionTokenService(Options.Create(ValidOptions()), time);
        var downloadId = Guid.NewGuid();
        var token = service.Issue(downloadId);

        Assert.True(service.Validate(downloadId, token));
        Assert.False(service.Validate(Guid.NewGuid(), token));
        Assert.False(service.Validate(downloadId, token[..^1] + (token[^1] == 'A' ? "B" : "A")));

        time.Advance(TimeSpan.FromMinutes(121));
        Assert.False(service.Validate(downloadId, token));
    }

    [Fact]
    public void Options_RequireAllowlistedOriginsAndStrongCompletionKey()
    {
        var production = new DownloadsOptionsValidator(new TestEnvironment(Environments.Production));
        var valid = ValidOptions();
        Assert.True(production.Validate(null, valid).Succeeded);

        var temporaryCache = ValidOptions();
        temporaryCache.CacheDirectory = Path.GetFullPath(Path.GetTempPath());
        Assert.True(production.Validate(null, temporaryCache).Failed);

        valid.AllowHttpArtifacts = true;
        valid.CompletionTokenKey = "weak";
        valid.MaximumConcurrentStreams = 0;
        valid.StreamBufferBytes = 1024;
        valid.StreamDeadlineMinutes = 0;
        Assert.True(production.Validate(null, valid).Failed);
    }

    [Theory]
    [InlineData("newsletter", "newsletter")]
    [InlineData("invalid source!", "other")]
    [InlineData("", "direct")]
    public void SourceNormalization_BoundsUntrustedCampaignData(string source, string expected) =>
        Assert.Equal(expected, DownloadRequestPrivacy.NormalizeSource(source));

    [Fact]
    public void UserAgent_StoresOnlyBoundedFamily()
    {
        Assert.Equal("PeerOnQ", DownloadRequestPrivacy.UserAgentFamily("PeerOnQ/1.0 private-device-id"));
        Assert.Equal("Other", DownloadRequestPrivacy.UserAgentFamily("custom-client private-device-id"));
        Assert.Null(DownloadRequestPrivacy.NormalizeCampaign("bad campaign with spaces"));
    }

    [Fact]
    public void PartialDownload_IsTerminalAndDoesNotMapToCompleted()
    {
        var now = DateTimeOffset.Parse("2026-08-11T10:00:00Z");
        var download = DownloadEvent.Start(
            Guid.NewGuid(),
            new byte[32],
            null,
            PlatformKind.Windows,
            ArchitectureKind.X64,
            "1.2.3",
            InstallChannel.Stable,
            "direct",
            null,
            null,
            "PeerOnQ",
            now);

        download.Complete(DownloadResult.Partial, now.AddSeconds(1));

        Assert.Equal(DownloadResult.Partial, download.Result);
        Assert.NotEqual(DownloadResult.Completed, download.Result);
    }

    private static DownloadsOptions ValidOptions() => new()
    {
        AllowedArtifactHosts = ["artifacts.peeronq.test"],
        CompletionTokenKey = new string('k', 64),
        CompletionTokenMinutes = 120,
    };

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
