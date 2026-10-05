using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanPong;

internal static class MdnsNetworkInterfaces
{
    private const int Ipv4ByteCount = 4;

    internal static IPAddress[] LocalInterfaceAddresses(bool includeLoopback)
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork &&
                            (includeLoopback || !IPAddress.IsLoopback(a)))
                .Distinct()
                .ToArray();
            if (addresses.Length == 0) return [IPAddress.Loopback];
            return addresses;
        }
        catch (NetworkInformationException) { return [IPAddress.Loopback]; }
    }

    internal static bool IsOnLocalSubnet(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var local in network.GetIPProperties().UnicastAddresses)
            {
                if (local.Address.AddressFamily != AddressFamily.InterNetwork || local.IPv4Mask is null) continue;
                var candidate = address.GetAddressBytes();
                var own = local.Address.GetAddressBytes();
                var mask = local.IPv4Mask.GetAddressBytes();
                if (Enumerable.Range(0, Ipv4ByteCount).All(i => (candidate[i] & mask[i]) == (own[i] & mask[i])))
                    return true;
            }
        }
        catch (NetworkInformationException) { }
        return false;
    }
}
