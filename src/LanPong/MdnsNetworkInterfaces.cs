using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanPong;

internal static class MdnsNetworkInterfaces
{
    private const int Ipv4ByteCount = 4;

    internal static IPAddress[] LocalInterfaceAddresses(bool includeLoopback,
        AddressFamily family = AddressFamily.InterNetwork)
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            (n.SupportsMulticast || family == AddressFamily.InterNetworkV6 && includeLoopback &&
                             n.NetworkInterfaceType == NetworkInterfaceType.Loopback))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == family &&
                            (includeLoopback || !IPAddress.IsLoopback(a)))
                .Distinct()
                .ToArray();
            if (addresses.Length == 0) return FallbackAddresses(includeLoopback, family);
            return addresses;
        }
        catch (NetworkInformationException) { return FallbackAddresses(includeLoopback, family); }
    }

    internal static int[] Ipv6InterfaceIndexes(bool includeLoopback)
    {
        try
        {
            var indexes = new HashSet<int>();
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    (!network.SupportsMulticast &&
                     !(includeLoopback && network.NetworkInterfaceType == NetworkInterfaceType.Loopback)) ||
                    (!includeLoopback && network.NetworkInterfaceType == NetworkInterfaceType.Loopback)) continue;

                try
                {
                    var properties = network.GetIPProperties();
                    if (!properties.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6))
                        continue;
                    var index = properties.GetIPv6Properties()?.Index;
                    if (index is > 0) indexes.Add(index.Value);
                }
                catch (NetworkInformationException) { }
            }
            return [.. indexes.Order()];
        }
        catch (NetworkInformationException) { return []; }
    }

    internal static bool IsOnLocalSubnet(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            var candidate = address.GetAddressBytes();
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                var properties = network.GetIPProperties();
                foreach (var local in properties.UnicastAddresses)
                {
                    if (local.Address.AddressFamily != address.AddressFamily) continue;
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (local.IPv4Mask is null) continue;
                        var own = local.Address.GetAddressBytes();
                        var mask = local.IPv4Mask.GetAddressBytes();
                        if (Enumerable.Range(0, Ipv4ByteCount)
                            .All(i => (candidate[i] & mask[i]) == (own[i] & mask[i]))) return true;
                    }
                    else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (address.IsIPv6LinkLocal)
                        {
                            if (address.ScopeId > 0 && local.Address.IsIPv6LinkLocal &&
                                (local.Address.ScopeId == address.ScopeId ||
                                 properties.GetIPv6Properties()?.Index == address.ScopeId)) return true;
                        }
                        else if (local.PrefixLength is > 0 and <= 128 &&
                                 SamePrefix(candidate, local.Address.GetAddressBytes(), local.PrefixLength))
                            return true;
                    }
                }
            }
        }
        catch (NetworkInformationException) { }
        return false;
    }

    private static IPAddress[] FallbackAddresses(bool includeLoopback, AddressFamily family) => family switch
    {
        AddressFamily.InterNetwork => [IPAddress.Loopback],
        AddressFamily.InterNetworkV6 when includeLoopback => [IPAddress.IPv6Loopback],
        _ => []
    };

    private static bool SamePrefix(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int length)
    {
        var wholeBytes = length / 8;
        if (!left[..wholeBytes].SequenceEqual(right[..wholeBytes])) return false;
        var remainingBits = length % 8;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (left[wholeBytes] & mask) == (right[wholeBytes] & mask);
    }
}
