using PeerOnQ.Platform.Linux.Security;
using Xunit;

namespace PeerOnQ.Platform.Linux.Tests;

public sealed class SecretToolDeviceSecretStoreTests
{
    [Fact]
    public async Task SetAsync_PassesBase64SecretThroughStandardInput()
    {
        var runner = new RecordingRunner(new SecretToolResult(0, string.Empty));
        var store = new SecretToolDeviceSecretStore(runner);

        await store.SetAsync("device-private-key", [1, 2, 3, 4]);

        Assert.Equal("store", runner.Arguments![0]);
        Assert.DoesNotContain(runner.StandardInput!, runner.Arguments);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), runner.StandardInput);
        Assert.Contains("profile", runner.Arguments);
        Assert.Contains("default", runner.Arguments);
    }

    [Fact]
    public async Task TryGetAsync_DecodesStoredSecret()
    {
        var expected = new byte[] { 7, 8, 9 };
        var runner = new RecordingRunner(new SecretToolResult(0, Convert.ToBase64String(expected) + Environment.NewLine));
        var store = new SecretToolDeviceSecretStore(runner);

        var actual = await store.TryGetAsync("device-private-key");

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPERCASE")]
    [InlineData("contains space")]
    [InlineData("../escape")]
    public async Task Operations_RejectUnsafeSecretNames(string name)
    {
        var store = new SecretToolDeviceSecretStore(new RecordingRunner(new SecretToolResult(0, string.Empty)));

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryGetAsync(name));
    }

    [Fact]
    public async Task TryGetAsync_FailsClosedForMalformedKeyringValue()
    {
        var store = new SecretToolDeviceSecretStore(new RecordingRunner(new SecretToolResult(0, "not-base64")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryGetAsync("device-private-key"));
    }

    private sealed class RecordingRunner(SecretToolResult result) : ISecretToolRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public string? StandardInput { get; private set; }

        public Task<SecretToolResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            Arguments = arguments;
            StandardInput = standardInput;
            return Task.FromResult(result);
        }
    }
}
