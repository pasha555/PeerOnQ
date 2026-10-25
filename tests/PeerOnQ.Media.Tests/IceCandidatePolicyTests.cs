using System.Net;
using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class IceCandidatePolicyTests
{
    [Theory]
    [InlineData("vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", true)]
    [InlineData("Ethernet 3", "VirtualBox Host-Only Ethernet Adapter", true)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter #2", true)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", false)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", false)]
    [InlineData("VPN", "WireGuard Tunnel", false)]
    public void Only_host_only_virtual_adapters_are_withheld(string name, string description, bool isVirtual) =>
        Assert.Equal(isVirtual, IceCandidatePolicy.IsVirtualAdapter(name, description));

    [Fact]
    public void Host_candidates_on_virtual_addresses_are_withheld_and_everything_else_is_advertised()
    {
        // The field PC: Wi-Fi 10.0.0.10, VPN 10.199.1.5, WSL 172.20.32.1, VMnet8 192.168.64.1.
        var virtualAddresses = new HashSet<IPAddress> { IPAddress.Parse("172.20.32.1"), IPAddress.Parse("192.168.64.1") };

        Assert.True(IceCandidatePolicy.ShouldAdvertise(
            "candidate:1 1 udp 2130706431 10.0.0.10 50000 typ host generation 0", virtualAddresses));
        Assert.True(IceCandidatePolicy.ShouldAdvertise(
            "candidate:2 1 udp 2130706431 10.199.1.5 50000 typ host generation 0", virtualAddresses));
        Assert.False(IceCandidatePolicy.ShouldAdvertise(
            "candidate:3 1 udp 2130706431 172.20.32.1 50000 typ host generation 0", virtualAddresses));
        Assert.False(IceCandidatePolicy.ShouldAdvertise(
            "candidate:4 1 udp 2130706431 192.168.64.1 50000 typ host generation 0", virtualAddresses));
        // Reflexive and relay candidates, and unparsable lines, are never withheld.
        Assert.True(IceCandidatePolicy.ShouldAdvertise(
            "candidate:5 1 udp 1694498815 172.20.32.1 50000 typ srflx raddr 0.0.0.0 rport 0", virtualAddresses));
        Assert.True(IceCandidatePolicy.ShouldAdvertise(
            "candidate:6 1 udp 16777215 192.168.64.1 50000 typ relay raddr 0.0.0.0 rport 0", virtualAddresses));
        Assert.True(IceCandidatePolicy.ShouldAdvertise("candidate:7 1 udp 1 host.local 1 typ host", virtualAddresses));
        Assert.True(IceCandidatePolicy.ShouldAdvertise("garbage", virtualAddresses));
    }
}
