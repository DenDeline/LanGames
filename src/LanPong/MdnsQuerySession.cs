using System.Net;
using System.Net.Sockets;

namespace LanPong;

/// <summary>Collects DNS-SD answers for one browse request.</summary>
internal sealed class MdnsQuerySession(string serviceType, string ownInstanceName,
    Func<IPAddress, bool> isOnLocalSubnet)
{
    private readonly Dictionary<string, FoundInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _withdrawn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(IPAddress Address, IPAddress Source)>> _addresses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queried = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<(string Name, ushort Type)> Observe(MdnsPacketCodec.Record record, IPAddress source)
    {
        if (record.Type == MdnsPacketCodec.Ptr && SameName(record.Name, serviceType) &&
            record is { Ttl: MdnsConstants.GoodbyeTtl, Target: { } retiredInstance })
        {
            _instances.Remove(retiredInstance);
            _withdrawn.Add(retiredInstance);
            return [];
        }

        if (record.Ttl == MdnsConstants.GoodbyeTtl) return [];

        switch (record.Type)
        {
            case MdnsPacketCodec.Ptr when SameName(record.Name, serviceType) &&
                                          record.Target is { } instanceName:
            {
                _withdrawn.Remove(instanceName);
                if (!_instances.TryGetValue(instanceName, out var instance))
                    _instances[instanceName] = instance = new FoundInstance();
                instance.SeenPtr = true;
                // Some responders omit SRV/TXT/address records from the PTR response.
                return _queried.Add(instanceName)
                    ? [(instanceName, MdnsPacketCodec.Srv), (instanceName, MdnsPacketCodec.Txt)]
                    : [];
            }
            case MdnsPacketCodec.Srv when record.Target is { } target &&
                                          record.Name.EndsWith("." + serviceType, StringComparison.OrdinalIgnoreCase) &&
                                          !_withdrawn.Contains(record.Name):
            {
                if (!_instances.TryGetValue(record.Name, out var instance))
                    _instances[record.Name] = instance = new FoundInstance();
                instance.Port = record.Port;
                instance.Target = target;
                return _queried.Add(target)
                    ? [(target, MdnsPacketCodec.A), (target, MdnsPacketCodec.Aaaa)]
                    : [];
            }
            case MdnsPacketCodec.Txt when
                record.Name.EndsWith("." + serviceType, StringComparison.OrdinalIgnoreCase) &&
                !_withdrawn.Contains(record.Name):
            {
                if (!_instances.TryGetValue(record.Name, out var instance))
                    _instances[record.Name] = instance = new FoundInstance();
                instance.Version = record.Version;
                instance.Nickname = record.Nickname;
                break;
            }
            case MdnsPacketCodec.A or MdnsPacketCodec.Aaaa when record.Address is { } ip &&
                (record.Type == MdnsPacketCodec.A && ip.AddressFamily == AddressFamily.InterNetwork ||
                 record.Type == MdnsPacketCodec.Aaaa && ip.AddressFamily == AddressFamily.InterNetworkV6):
            {
                var address = ip.IsIPv6LinkLocal && source.AddressFamily == AddressFamily.InterNetworkV6 &&
                              source.ScopeId > 0 && ip.GetAddressBytes().AsSpan().SequenceEqual(source.GetAddressBytes())
                    ? new IPAddress(ip.GetAddressBytes(), source.ScopeId)
                    : ip;
                if (!_addresses.TryGetValue(record.Name, out var candidates))
                    _addresses[record.Name] = candidates = [];
                if (!candidates.Any(item => item.Address.Equals(address) && item.Source.Equals(source)))
                    candidates.Add((address, source));
                break;
            }
        }
        return [];
    }

    public IReadOnlyList<DiscoveredHost> GetResults()
    {
        var results = new Dictionary<string, DiscoveredHost>(StringComparer.Ordinal);
        foreach (var (name, instance) in _instances)
        {
            if (!instance.SeenPtr || _withdrawn.Contains(name) || SameName(name, ownInstanceName) ||
                instance.Version != WirePacket.CurrentVersion ||
                !PlayerNickname.IsValid(instance.Nickname) ||
                instance.Port is < 1 or > ushort.MaxValue || instance.Target is null ||
                !_addresses.TryGetValue(instance.Target, out var candidates)) continue;

            // A multi-homed host may advertise several addresses. Prefer the
            // address matching the response source, then one on the same family/link.
            var usable = candidates.Where(item => !item.Address.Equals(IPAddress.Any) &&
                !item.Address.Equals(IPAddress.IPv6Any) &&
                (!item.Address.IsIPv6LinkLocal || item.Address.ScopeId > 0)).ToArray();
            var chosen = usable.FirstOrDefault(item => item.Address.Equals(item.Source)).Address
                ?? usable.FirstOrDefault(item => item.Address.AddressFamily == item.Source.AddressFamily &&
                                                 isOnLocalSubnet(item.Address)).Address
                ?? usable.Select(item => item.Address).FirstOrDefault(isOnLocalSubnet);
            if (chosen is null) continue;
            var host = new DiscoveredHost(chosen.ToString(), instance.Port, instance.Nickname!, name);
            results[$"{host.Address}:{host.Port}"] = host;
        }
        return [.. results.Values];
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class FoundInstance
    {
        public bool SeenPtr { get; set; }
        public int Port { get; set; }
        public string? Target { get; set; }
        public int? Version { get; set; }
        public string? Nickname { get; set; }
    }
}
