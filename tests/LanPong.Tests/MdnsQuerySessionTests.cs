using System.Net;

namespace LanPong.Tests;

public sealed class MdnsQuerySessionTests
{
    private const string Service = "_lanpong._udp.local.";
    private const string OwnInstance = "Own._lanpong._udp.local.";
    private const string PeerInstance = "Peer._lanpong._udp.local.";
    private const string PeerHost = "peer.local.";
    private static readonly IPAddress Source = IPAddress.Parse("192.168.1.44");

    [Test]
    public async Task Observe_OutOfOrderAnswersRequirePtrAndQueryMissingRecordsOnlyOnce()
    {
        var session = NewSession(_ => false);
        var address = IPAddress.Parse("192.168.1.44");

        session.Observe(Address(PeerHost, address), Source);
        session.Observe(Txt(PeerInstance), Source);
        var srvQueries = session.Observe(Srv(PeerInstance, PeerHost, 47888), Source);

        await Assert.That(session.GetResults().Count).IsEqualTo(0);
        await Assert.That(srvQueries.Count).IsEqualTo(2);
        await Assert.That(srvQueries[0]).IsEqualTo((PeerHost, MdnsPacketCodec.A));
        await Assert.That(srvQueries[1]).IsEqualTo((PeerHost, MdnsPacketCodec.Aaaa));

        var ptrQueries = session.Observe(Ptr(PeerInstance), Source);
        await Assert.That(ptrQueries.Count).IsEqualTo(2);
        await Assert.That(ptrQueries[0]).IsEqualTo((PeerInstance, MdnsPacketCodec.Srv));
        await Assert.That(ptrQueries[1]).IsEqualTo((PeerInstance, MdnsPacketCodec.Txt));
        await Assert.That(session.Observe(Ptr(PeerInstance), Source).Count).IsEqualTo(0);
        await Assert.That(session.Observe(Srv(PeerInstance, PeerHost, 47888), Source).Count).IsEqualTo(0);

        var hosts = session.GetResults();
        await Assert.That(hosts.Count).IsEqualTo(1);
        await Assert.That(hosts[0]).IsEqualTo(new DiscoveredHost(address.ToString(), 47888));
    }

    [Test]
    public async Task Observe_PtrGoodbyeWithdrawsInstanceDespiteOtherCachedAnswers()
    {
        var session = NewSession(_ => true);
        session.Observe(Ptr(PeerInstance), Source);
        session.Observe(Srv(PeerInstance, PeerHost, 47888), Source);
        session.Observe(Txt(PeerInstance), Source);
        session.Observe(Address(PeerHost, Source), Source);
        await Assert.That(session.GetResults().Count).IsEqualTo(1);

        session.Observe(Ptr(PeerInstance, ttl: 0), Source);
        session.Observe(Srv(PeerInstance, PeerHost, 47888), Source);
        session.Observe(Txt(PeerInstance), Source);
        await Assert.That(session.GetResults().Count).IsEqualTo(0);

        session.Observe(Ptr(PeerInstance), Source);
        session.Observe(Srv(PeerInstance, PeerHost, 47888), Source);
        session.Observe(Txt(PeerInstance), Source);
        await Assert.That(session.GetResults().Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetResults_FiltersOwnAndIncompatibleInstancesAndCollapsesSharedEndpoint()
    {
        var session = NewSession(_ => true);
        const string oldInstance = "Old._lanpong._udp.local.";
        const string secondPeer = "Second._lanpong._udp.local.";

        AddInstance(session, OwnInstance, PeerHost, 47888);
        AddInstance(session, oldInstance, PeerHost, 47888, WirePacket.CurrentVersion - 1);
        AddInstance(session, PeerInstance, PeerHost, 47888);
        AddInstance(session, secondPeer, PeerHost, 47888);
        session.Observe(Address(PeerHost, Source), Source);

        var hosts = session.GetResults();
        await Assert.That(hosts.Count).IsEqualTo(1);
        await Assert.That(hosts[0]).IsEqualTo(new DiscoveredHost(Source.ToString(), 47888));
    }

    [Test]
    public async Task GetResults_PrefersSourceMatchingAddressThenUsesLocalSubnetFallback()
    {
        var localAddress = IPAddress.Parse("192.168.1.45");
        var remoteAddress = IPAddress.Parse("198.51.100.45");
        var remoteSource = IPAddress.Parse("198.51.100.1");
        var session = NewSession(address => address.Equals(localAddress));
        AddInstance(session, PeerInstance, PeerHost, 47888);
        session.Observe(Address(PeerHost, localAddress), remoteSource);
        session.Observe(Address(PeerHost, remoteAddress), remoteAddress);

        var preferred = session.GetResults();
        await Assert.That(preferred.Count).IsEqualTo(1);
        await Assert.That(preferred[0].Address).IsEqualTo(remoteAddress.ToString());

        var fallback = NewSession(address => address.Equals(localAddress));
        AddInstance(fallback, PeerInstance, PeerHost, 47888);
        fallback.Observe(Address(PeerHost, remoteAddress), remoteSource);
        fallback.Observe(Address(PeerHost, localAddress), remoteSource);

        var hosts = fallback.GetResults();
        await Assert.That(hosts.Count).IsEqualTo(1);
        await Assert.That(hosts[0].Address).IsEqualTo(localAddress.ToString());
    }

    [Test]
    public async Task GetResults_UsesIpv6SourceAndScopesLinkLocalAaaaAddress()
    {
        var source = IPAddress.Parse("fe80::44%7");
        var session = NewSession(_ => true);
        AddInstance(session, PeerInstance, PeerHost, 47888, source: source);
        session.Observe(Address(PeerHost, IPAddress.Parse("192.168.1.44")), source);
        session.Observe(Address(PeerHost, IPAddress.Parse("fe80::44")), source);

        var hosts = session.GetResults();
        await Assert.That(hosts.Count).IsEqualTo(1);
        await Assert.That(hosts[0]).IsEqualTo(new DiscoveredHost(source.ToString(), 47888));
    }

    [Test]
    public async Task GetResults_ScopesOnlyLinkLocalAaaaMatchingIpv6Source()
    {
        var source = IPAddress.Parse("fe80::44%7");
        var session = NewSession(_ => true);
        AddInstance(session, PeerInstance, PeerHost, 47888, source: source);

        // An announcement can include link-local addresses from other interfaces.
        session.Observe(Address(PeerHost, IPAddress.Parse("fe80::99")), source);
        await Assert.That(session.GetResults().Count).IsEqualTo(0);

        session.Observe(Address(PeerHost, IPAddress.Parse("fe80::44")), source);
        await Assert.That(session.GetResults().Single())
            .IsEqualTo(new DiscoveredHost(source.ToString(), 47888));
    }

    [Test]
    public async Task GetResults_PrefersReachableIpv6AddressOverIpv4OnIpv6Link()
    {
        var source = IPAddress.Parse("fe80::1%7");
        var ipv6 = IPAddress.Parse("fd12:3456::44");
        var session = NewSession(_ => true);
        AddInstance(session, PeerInstance, PeerHost, 47888, source: source);
        session.Observe(Address(PeerHost, IPAddress.Parse("192.168.1.44")), source);
        session.Observe(Address(PeerHost, ipv6), source);

        var hosts = session.GetResults();
        await Assert.That(hosts.Count).IsEqualTo(1);
        await Assert.That(hosts[0].Address).IsEqualTo(ipv6.ToString());
    }

    [Test]
    public async Task GetResults_DoesNotReturnUnscopedLinkLocalIpv6Address()
    {
        var session = NewSession(_ => true);
        AddInstance(session, PeerInstance, PeerHost, 47888);
        session.Observe(Address(PeerHost, IPAddress.Parse("fe80::44")), Source);

        await Assert.That(session.GetResults().Count).IsEqualTo(0);

        var ipv4 = IPAddress.Parse("192.168.1.44");
        session.Observe(Address(PeerHost, ipv4), Source);
        await Assert.That(session.GetResults().Single().Address).IsEqualTo(ipv4.ToString());
    }

    private static MdnsQuerySession NewSession(Func<IPAddress, bool> isOnLocalSubnet) =>
        new(Service, OwnInstance, isOnLocalSubnet);

    private static void AddInstance(MdnsQuerySession session, string instance, string host, int port,
        int version = WirePacket.CurrentVersion, IPAddress? source = null)
    {
        var sender = source ?? Source;
        session.Observe(Ptr(instance), sender);
        session.Observe(Srv(instance, host, port), sender);
        session.Observe(Txt(instance, version), sender);
    }

    private static MdnsPacketCodec.Record Ptr(string instance, uint ttl = 120) =>
        new(Service, MdnsPacketCodec.Ptr, ttl, Target: instance);

    private static MdnsPacketCodec.Record Srv(string instance, string host, int port) =>
        new(instance, MdnsPacketCodec.Srv, 120, Target: host, Port: port);

    private static MdnsPacketCodec.Record Txt(string instance, int version = WirePacket.CurrentVersion) =>
        new(instance, MdnsPacketCodec.Txt, 120, Version: version);

    private static MdnsPacketCodec.Record Address(string host, IPAddress address) =>
        new(host, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? MdnsPacketCodec.A : MdnsPacketCodec.Aaaa, 120, Address: address);
}
