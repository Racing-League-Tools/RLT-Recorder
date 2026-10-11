using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RltUdpClient.Core;

/// <summary>An IPv4 address of this machine and the adapter it belongs to.</summary>
public sealed record LocalAddress(string Address, string Adapter)
{
    public override string ToString() => $"{Address} ({Adapter})";
}

/// <summary>
/// This machine's addresses, sorted into the ones to give a game or a phone on
/// the same network and the rest.
///
/// <see cref="Lan"/> holds addresses on adapters with a default gateway — the
/// home network, where a console or another PC can reach the recorder. A
/// machine can be on two at once (cable and Wi-Fi); both are listed, since only
/// the user knows which one the console is on. <see cref="Other"/> holds VPN and
/// overlay addresses (Tailscale, WireGuard, ZeroTier…), useful when telemetry is
/// forwarded over one, but confusing as the main answer. Link-local 169.254.x.x,
/// loopback and the virtual switches of Hyper-V, WSL, Docker and VM software are
/// left out entirely: nothing outside the machine can use them.
/// </summary>
public sealed record NetworkAddresses(IReadOnlyList<LocalAddress> Lan, IReadOnlyList<LocalAddress> Other)
{
    private static readonly string[] VirtualSwitch =
        ["vethernet", "hyper-v", "wsl", "docker", "virtualbox", "vboxnet", "vmware", "vmnet", "virbr", "veth", "br-"];

    private static readonly string[] Vpn =
        ["tailscale", "wireguard", "wg", "nordlynx", "openvpn", "tap", "tun", "utun", "zerotier", "zt", "vpn", "ppp"];

    public static NetworkAddresses None { get; } = new([], []);

    /// <summary>The address to show first, or null without a network.</summary>
    public LocalAddress? Primary => Lan.Count > 0 ? Lan[0] : Other.Count > 0 ? Other[0] : null;

    /// <summary>
    /// Purely informational, so every failure gives an empty answer instead of
    /// an exception: enumerating interfaces needs a netlink socket on Linux,
    /// which a sandboxed service or a locked-down container may refuse, and a
    /// recorder must not die over what it shows.
    /// </summary>
    public static NetworkAddresses Current()
    {
        var lan = new List<LocalAddress>();
        var other = new List<LocalAddress>();

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception)
        {
            return None;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var label = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            if (VirtualSwitch.Any(label.Contains))
                continue;

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (Exception)
            {
                continue;
            }

            var isVpn = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                        || Vpn.Any(word => NameHas(nic, word));
            var hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork
                    || IPAddress.IsLoopback(address) || IsLinkLocal(address))
                    continue;

                (hasGateway && !isVpn ? lan : other).Add(new LocalAddress(address.ToString(), nic.Name));
            }
        }

        return new NetworkAddresses(
            lan.DistinctBy(a => a.Address).ToArray(),
            other.DistinctBy(a => a.Address).Where(o => lan.All(l => l.Address != o.Address)).ToArray());
    }

    /// <summary>
    /// Whole-word match on the adapter name or description, so "tun" finds
    /// "tun0" and "TUN adapter" but not "Tunnel-free Ethernet".
    /// </summary>
    private static bool NameHas(NetworkInterface nic, string word)
    {
        foreach (var text in new[] { nic.Name, nic.Description })
        {
            var lower = text.ToLowerInvariant();
            for (var at = lower.IndexOf(word, StringComparison.Ordinal); at >= 0;
                 at = lower.IndexOf(word, at + 1, StringComparison.Ordinal))
            {
                var startsWord = at == 0 || !char.IsLetter(lower[at - 1]);
                var end = at + word.Length;
                var endsWord = end == lower.Length || !char.IsLetter(lower[end]);
                if (startsWord && endsWord)
                    return true;
            }
        }

        return false;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
