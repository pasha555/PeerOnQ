using System.Runtime.InteropServices;
using Xunit;

namespace PeerOnQ.App.Linux.Tests;

public sealed class LinuxNativeLibraryBootstrapTests
{
    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public void DecoderCandidatesUseOnlyFixedSystemPathsAndTheReviewedSoname(Architecture architecture)
    {
        var candidates = LinuxNativeLibraryBootstrap.CandidatePaths(architecture);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, path =>
        {
            Assert.StartsWith("/", path);
            Assert.EndsWith("/libvpx.so.9", path);
            Assert.DoesNotContain("..", path);
            Assert.DoesNotContain("peeronq", path, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void UnsupportedArchitectureHasNoNativeLibraryFallback() =>
        Assert.Empty(LinuxNativeLibraryBootstrap.CandidatePaths(Architecture.X86));
}
