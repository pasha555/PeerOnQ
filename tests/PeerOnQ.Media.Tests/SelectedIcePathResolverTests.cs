using System.Net;
using System.Net.Sockets;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class SelectedIcePathResolverTests
{
    [Theory]
    [InlineData("10.0.0.1", "192.168.1.2", ConnectionPath.DirectLan)]
    [InlineData("172.16.0.1", "169.254.1.2", ConnectionPath.DirectLan)]
    [InlineData("127.0.0.1", "127.0.0.1", ConnectionPath.DirectLan)]
    [InlineData("fc00::1", "fd00::2", ConnectionPath.DirectLan)]
    [InlineData("fe80::1", "fe80::2", ConnectionPath.DirectLan)]
    [InlineData("::1", "::1", ConnectionPath.DirectLan)]
    [InlineData("10.0.0.1", "8.8.8.8", ConnectionPath.DirectInternet)]
    [InlineData("100.64.0.1", "10.0.0.2", ConnectionPath.DirectInternet)]
    [InlineData("172.32.0.1", "10.0.0.2", ConnectionPath.DirectInternet)]
    [InlineData("2001:db8::1", "fd00::2", ConnectionPath.DirectInternet)]
    [InlineData("host.invalid", "10.0.0.2", ConnectionPath.UnknownNegotiating)]
    [InlineData("10.0.0.1", "host.invalid", ConnectionPath.UnknownNegotiating)]
    [InlineData("10.0.0.1", "::", ConnectionPath.UnknownNegotiating)]
    public void Explicit_endpoints_preserve_existing_private_policy_without_OS_probes(
        string local, string remote, ConnectionPath expected)
    {
        var resolver = new SelectedIcePathResolver(
            _ => throw new InvalidOperationException("Explicit addresses must not probe a route."),
            _ => throw new InvalidOperationException("Explicit addresses must not enumerate interfaces."));

        Assert.Equal(expected, resolver.Classify(new object(), local, remote, 5000));
    }

    [Theory]
    [InlineData("::")]
    [InlineData("0.0.0.0")]
    public void Wildcard_uses_OS_selected_exact_active_local_address(string wildcard)
    {
        var selected = IPAddress.Parse("10.0.0.1");
        var routeCalls = 0;
        var interfaceCalls = 0;
        var resolver = new SelectedIcePathResolver(remote =>
        {
            routeCalls++;
            Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.2"), 5000), remote);
            return selected;
        }, address =>
        {
            interfaceCalls++;
            Assert.Same(selected, address);
            return true;
        });

        Assert.Equal(ConnectionPath.DirectLan, resolver.Classify(new object(), wildcard, "10.0.0.2", 5000));
        Assert.Equal(1, routeCalls);
        Assert.Equal(1, interfaceCalls);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("::", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("fd00::1", true)]
    [InlineData("10.0.0.1", false)]
    public void Missing_wildcard_wrong_family_or_unassigned_route_fails_closed(string? selected, bool active)
    {
        var resolver = new SelectedIcePathResolver(
            _ => selected is null ? null : IPAddress.Parse(selected), _ => active);

        Assert.Equal(ConnectionPath.UnknownNegotiating,
            resolver.Classify(new object(), "::", "10.0.0.2", 5000));
    }

    [Theory]
    [InlineData("10.0.0.1", "8.8.8.8")]
    [InlineData("8.8.8.8", "10.0.0.2")]
    [InlineData("fd00::1", "2001:db8::2")]
    public void Wildcard_route_does_not_broaden_private_address_policy(string local, string remote)
    {
        var resolver = new SelectedIcePathResolver(_ => IPAddress.Parse(local), _ => true);
        Assert.Equal(ConnectionPath.DirectInternet, resolver.Classify(new object(), "::", remote, 5000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Invalid_wildcard_remote_port_never_probes(int port)
    {
        var resolver = new SelectedIcePathResolver(_ => throw new InvalidOperationException("Must not probe."));
        Assert.Equal(ConnectionPath.UnknownNegotiating, resolver.Classify(new object(), "::", "10.0.0.2", port));
    }

    [Fact]
    public void Route_and_interface_failure_are_closed_and_do_not_escape_statistics_query()
    {
        var routeFailure = new SelectedIcePathResolver(_ => throw new SocketException());
        var interfaceFailure = new SelectedIcePathResolver(_ => IPAddress.Parse("10.0.0.1"),
            _ => throw new System.Net.NetworkInformation.NetworkInformationException());

        Assert.Equal(ConnectionPath.UnknownNegotiating,
            routeFailure.Classify(new object(), "::", "10.0.0.2", 5000));
        Assert.Equal(ConnectionPath.UnknownNegotiating,
            interfaceFailure.Classify(new object(), "::", "10.0.0.2", 5000));
    }

    [Fact]
    public void Cache_is_one_short_lived_nomination_endpoint_result_and_rechecks_active_interface()
    {
        var clock = new ManualClock();
        var active = true;
        var calls = 0;
        var nomination = new object();
        var resolver = new SelectedIcePathResolver(_ =>
        {
            calls++;
            return IPAddress.Parse("10.0.0.1");
        }, _ => active, clock);

        Assert.Equal(ConnectionPath.DirectLan, resolver.Classify(nomination, "::", "10.0.0.2", 5000));
        active = false;
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(ConnectionPath.DirectLan, resolver.Classify(nomination, "::", "10.0.0.2", 5000));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(ConnectionPath.UnknownNegotiating, resolver.Classify(nomination, "::", "10.0.0.2", 5000));
        Assert.Equal(2, calls);
        active = true;
        Assert.Equal(ConnectionPath.DirectLan, resolver.Classify(new object(), "::", "10.0.0.2", 5000));
        Assert.Equal(ConnectionPath.DirectLan, resolver.Classify(nomination, "::", "10.0.0.2", 5001));
        Assert.Equal(ConnectionPath.DirectLan, resolver.Classify(nomination, "::", "10.0.0.3", 5001));
        Assert.Equal(5, calls);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(TimeSpan elapsed) => _milliseconds += (long)elapsed.TotalMilliseconds;
    }
}
