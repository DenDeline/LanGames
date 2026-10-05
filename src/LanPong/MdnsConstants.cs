using System.Net;

namespace LanPong;

/// <summary>Service identity, transport settings, and timing for LAN discovery.</summary>
internal static class MdnsConstants
{
    public const string ServiceType = "_lanpong._udp.local.";
    public const int Port = 5353;
    public const int MulticastHopLimit = 255;
    public const int ListenerCapacity = 64;
    public const uint ServiceRecordTtl = 120;
    public const uint LegacyResponseTtl = 10;
    public const uint GoodbyeTtl = 0;

    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    public static readonly IPEndPoint GroupEndpoint = new(Group, Port);
    public static readonly TimeSpan AnnouncementInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DiscoveryResponseWindow = TimeSpan.FromMilliseconds(1300);
}
