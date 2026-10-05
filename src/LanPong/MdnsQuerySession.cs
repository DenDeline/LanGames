using System.Net;

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
                // Some responders omit SRV/TXT/A from the PTR response.
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
                return _queried.Add(target) ? [(target, MdnsPacketCodec.A)] : [];
            }
            case MdnsPacketCodec.Txt when
                record.Name.EndsWith("." + serviceType, StringComparison.OrdinalIgnoreCase) &&
                !_withdrawn.Contains(record.Name):
            {
                if (!_instances.TryGetValue(record.Name, out var instance))
                    _instances[record.Name] = instance = new FoundInstance();
                instance.Version = record.Version;
                break;
            }
            case MdnsPacketCodec.A when record.Address is { } ip:
            {
                if (!_addresses.TryGetValue(record.Name, out var candidates))
                    _addresses[record.Name] = candidates = [];
                if (!candidates.Any(item => item.Address.Equals(ip) && item.Source.Equals(source)))
                    candidates.Add((ip, source));
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
                instance.Port is < 1 or > ushort.MaxValue || instance.Target is null ||
                !_addresses.TryGetValue(instance.Target, out var candidates)) continue;

            // A multi-homed host may advertise several addresses. The A record
            // that matches the response's source is on the observed local link.
            var chosen = candidates.FirstOrDefault(item => item.Address.Equals(item.Source)).Address
                ?? candidates.Select(item => item.Address).FirstOrDefault(isOnLocalSubnet);
            if (chosen is null || chosen.Equals(IPAddress.Any)) continue;
            var host = new DiscoveredHost(chosen.ToString(), instance.Port);
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
    }
}
