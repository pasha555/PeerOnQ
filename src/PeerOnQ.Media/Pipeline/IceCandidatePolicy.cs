using System.Net;
using System.Net.NetworkInformation;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// Which local ICE candidates are advertised to the peer. Host candidates of host-only virtual
/// networks (Hyper-V/WSL vEthernet, VMware VMnet, VirtualBox host-only, Docker) never reach another
/// device, yet each one adds failing pairs and TURN permissions that delay the direct LAN pair; a
/// field session between two devices on one Wi-Fi went through TURN with up to 40% loss. VPN
/// adapters stay advertised: a peer may be reachable only through them.
/// </summary>
public static class IceCandidatePolicy
{
    private static readonly string[] VirtualAdapterMarkers =
    [
        "Hyper-V Virtual", "vEthernet", "VMware", "VMnet", "VirtualBox", "Host-Only",
        "WSL", "Docker", "Npcap Loopback", "Loopback Pseudo",
    ];

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static (DateTimeOffset At, IReadOnlySet<IPAddress> Addresses)? _cache;

    public static bool IsVirtualAdapter(string name, string description) =>
        VirtualAdapterMarkers.Any(marker =>
            name.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>False only for a host candidate on one of <paramref name="virtualAddresses"/>.</summary>
    public static bool ShouldAdvertise(string candidate, IReadOnlySet<IPAddress> virtualAddresses) =>
        !TryParseHost(candidate, out var address) || !virtualAddresses.Contains(address);

    public static bool ShouldAdvertise(string candidate) => ShouldAdvertise(candidate, VirtualAdapterAddresses());

    /// <summary>"candidate:f 1 udp p ADDRESS port typ host ..." with an IP literal address.</summary>
    internal static bool TryParseHost(string candidate, out IPAddress address)
    {
        address = IPAddress.None;
        var fields = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 8
               && string.Equals(fields[6], "typ", StringComparison.OrdinalIgnoreCase)
               && string.Equals(fields[7], "host", StringComparison.OrdinalIgnoreCase)
               && IPAddress.TryParse(fields[4], out address!);
    }

    public static IReadOnlySet<IPAddress> VirtualAdapterAddresses()
    {
        lock (Gate)
        {
            if (_cache is { } cached && DateTimeOffset.UtcNow - cached.At < CacheLifetime) return cached.Addresses;
            var addresses = new HashSet<IPAddress>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!IsVirtualAdapter(adapter.Name, adapter.Description)) continue;
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                        addresses.Add(unicast.Address);
                }
            }
            catch (NetworkInformationException)
            {
                // Without the adapter list every candidate is advertised, as before.
            }

            _cache = (DateTimeOffset.UtcNow, addresses);
            return addresses;
        }
    }

    /// <summary>The adapter that owns a local address, for diagnostics only.</summary>
    public static string? AdapterNameOf(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed)) return null;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(adapter => adapter.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(parsed)))
                ?.Name;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
