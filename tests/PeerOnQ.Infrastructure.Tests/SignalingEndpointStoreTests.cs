using PeerOnQ.Infrastructure;
using PeerOnQ.Infrastructure.Configuration;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

public sealed class SignalingEndpointStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "peeronq-signaling-settings-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("wss://signal.example.com", "wss://signal.example.com/ws")]
    [InlineData("wss://signal.example.com/ws", "wss://signal.example.com/ws")]
    [InlineData("ws://127.0.0.1:5080/ws", "ws://127.0.0.1:5080/ws")]
    public void Endpoint_is_normalized(string input, string expected)
    {
        Assert.Equal(expected, SignalingEndpointStore.ParseAndNormalize(input).AbsoluteUri);
    }

    [Theory]
    [InlineData("http://signal.example.com/ws")]
    [InlineData("ws://192.168.1.10:5080/ws")]
    [InlineData("wss://user:secret@signal.example.com/ws")]
    [InlineData("wss://signal.example.com/other")]
    [InlineData("wss://signal.example.com/ws?token=secret")]
    public void Unsafe_or_ambiguous_endpoint_is_rejected(string input)
    {
        Assert.Throws<ArgumentException>(() => SignalingEndpointStore.ParseAndNormalize(input));
    }

    [Fact]
    public async Task Saved_endpoint_is_resolved_after_a_new_store_instance()
    {
        var paths = new PeerOnQPaths(_root).EnsureCreated();
        var store = new SignalingEndpointStore(paths, "wss://default.example.com/ws");

        await store.SaveAsync("wss://signal.example.com");

        var resolved = new SignalingEndpointStore(paths, "wss://default.example.com/ws").Resolve();
        Assert.Equal("wss://signal.example.com/ws", resolved.Uri.AbsoluteUri);
        Assert.Equal(SignalingEndpointSource.LocalSettings, resolved.Source);
    }

    [Fact]
    public void Oversized_saved_configuration_is_rejected()
    {
        var paths = new PeerOnQPaths(_root).EnsureCreated();
        File.WriteAllBytes(paths.ConnectionSettingsFile, new byte[SignalingEndpointStore.MaxSerializedBytes + 1]);

        var store = new SignalingEndpointStore(paths, "wss://default.example.com/ws");

        Assert.Throws<InvalidOperationException>(() => store.Resolve());
    }

    [Fact]
    public async Task Official_release_ignores_saved_endpoint_and_rejects_changes()
    {
        var paths = new PeerOnQPaths(_root).EnsureCreated();
        var development = new SignalingEndpointStore(paths, "wss://official.example.com/ws");
        await development.SaveAsync("wss://custom.example.com/ws");

        var official = new SignalingEndpointStore(
            paths,
            "wss://official.example.com/ws",
            allowDevelopmentOverrides: false);

        Assert.Equal(SignalingEndpointSource.CompiledDefault, official.Resolve().Source);
        Assert.Equal("wss://official.example.com/ws", official.Resolve().Uri.AbsoluteUri);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            official.SaveAsync("wss://attacker.example.com/ws"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
