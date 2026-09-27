using System.Net.WebSockets;
using PeerOnQ.Signaling.Server.Security;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public class DevicePinStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "peeronq-pins", Guid.NewGuid().ToString("N"), "pins.json");

    [Fact]
    public void A_pin_survives_a_restart_of_the_registry()
    {
        var path = TempPath();
        var store = new FileDevicePinStore(path);

        var first = new DevicePublicKeyRegistry(store);
        Assert.True(first.TryPinOrMatch("LNK-483-921-756-204", "key-a"));

        // A brand new registry reading the same file stands in for a server restart.
        var second = new DevicePublicKeyRegistry(new FileDevicePinStore(path));

        Assert.Equal(1, second.Count);
        Assert.True(second.TryPinOrMatch("LNK-483-921-756-204", "key-a"));
        Assert.False(second.TryPinOrMatch("LNK-483-921-756-204", "key-b"));

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Only_public_keys_are_written_to_disk()
    {
        var path = TempPath();
        var registry = new DevicePublicKeyRegistry(new FileDevicePinStore(path));

        registry.TryPinOrMatch("LNK-483-921-756-204", "public-key-material");

        var contents = File.ReadAllText(path);

        Assert.Contains("public-key-material", contents);
        Assert.Contains("LNK-483-921-756-204", contents);
        Assert.DoesNotContain("PRIVATE", contents, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void A_corrupt_pin_file_does_not_stop_the_server()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        var registry = new DevicePublicKeyRegistry(new FileDevicePinStore(path));

        Assert.Equal(0, registry.Count);
        Assert.True(registry.TryPinOrMatch("LNK-483-921-756-204", "key-a"));

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Unpinning_lets_a_reprovisioned_device_register_again()
    {
        var registry = new DevicePublicKeyRegistry();

        registry.TryPinOrMatch("LNK-483-921-756-204", "old-key");
        Assert.False(registry.TryPinOrMatch("LNK-483-921-756-204", "new-key"));

        Assert.True(registry.Unpin("LNK-483-921-756-204"));
        Assert.True(registry.TryPinOrMatch("LNK-483-921-756-204", "new-key"));
    }

    [Fact]
    public void In_memory_pins_do_not_survive_a_restart()
    {
        var first = new DevicePublicKeyRegistry(new InMemoryDevicePinStore());
        first.TryPinOrMatch("LNK-483-921-756-204", "key-a");

        var second = new DevicePublicKeyRegistry(new InMemoryDevicePinStore());

        Assert.Equal(0, second.Count);
    }
}

public class TlsEnforcementTests
{
    [Fact]
    public async Task Loopback_is_allowed_over_plain_http()
    {
        await using var harness = await SignalingHarness.StartAsync(o => o.RequireTlsOutsideLoopback = true);

        using var http = new HttpClient();
        var response = await http.GetAsync(harness.HealthUri);

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_loopback_websocket_still_connects_with_tls_enforcement_on()
    {
        await using var harness = await SignalingHarness.StartAsync(o => o.RequireTlsOutsideLoopback = true);
        using var device = new TestDevice("Sharer PC");
        await using var client = device.CreateClient(harness.WebSocketUri);

        await client.ConnectAsync(device.Identity);

        Assert.Equal(PeerOnQ.Application.Abstractions.SignalingConnectionState.Registered, client.State);
    }

    [Fact]
    public async Task The_client_refuses_plain_ws_to_a_non_loopback_host()
    {
        using var device = new TestDevice("Sharer PC");
        await using var client = device.CreateClient(new Uri("ws://198.51.100.7:5080/ws"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(device.Identity));

        Assert.Contains("wss://", error.Message);
    }
}
