using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;

namespace LanPong;

/// <summary>IPv4 and IPv6 DNS-SD for games waiting on the local link.</summary>
internal sealed class MdnsDiscovery : IAsyncDisposable
{
    private readonly string _instanceName = $"LanPong-{Guid.NewGuid():N}";
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _advertiseSignal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Channel<MdnsDatagram>> _listeners = [];
    private readonly UdpClient? _ipv4Socket;
    private readonly UdpClient? _ipv6Socket;
    private readonly Task[] _receiveTasks;
    private readonly Task _advertiseTask;
    private int? _desiredPort;
    private string _desiredNickname = "Игрок";
    private int? _publishedPort;
    private string? _publishedNickname;
    private bool _disposed;

    private string FullInstanceName => $"{_instanceName}.{MdnsConstants.ServiceType}";
    internal string InstanceName => FullInstanceName;
    private string HostName => $"{_instanceName.ToLowerInvariant()}.local.";

    public MdnsDiscovery(ILogger logger)
    {
        _logger = logger;
        _ipv4Socket = CreateIpv4Socket();
        _ipv6Socket = CreateIpv6Socket();
        _receiveTasks = new[] { _ipv4Socket, _ipv6Socket }
            .Where(socket => socket is not null)
            .Select(socket => ReceiveAsync(socket!, _stop.Token))
            .ToArray();
        _advertiseTask = _receiveTasks.Length == 0 ? Task.CompletedTask : AdvertiseAsync(_stop.Token);
    }

    private UdpClient? CreateIpv4Socket()
    {
        UdpClient? socket = null;
        try
        {
            socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsConstants.Port));
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive,
                MdnsConstants.MulticastHopLimit);
            socket.MulticastLoopback = true;

            var joined = 0;
            foreach (var address in MdnsNetworkInterfaces.LocalInterfaceAddresses(includeLoopback: true))
            {
                try
                {
                    socket.JoinMulticastGroup(MdnsConstants.Group, address);
                    joined++;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "mDNS cannot join on {Address}", address);
                }
            }
            if (joined == 0) socket.JoinMulticastGroup(MdnsConstants.Group);
            return socket;
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException)
        {
            _logger.LogWarning(ex, "IPv4 mDNS is unavailable; connect by IP address instead");
            socket?.Dispose();
            return null;
        }
    }

    private UdpClient? CreateIpv6Socket()
    {
        if (!Socket.OSSupportsIPv6) return null;
        UdpClient? socket = null;
        try
        {
            socket = new UdpClient(AddressFamily.InterNetworkV6);
            socket.Client.DualMode = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, MdnsConstants.Port));
            socket.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive,
                MdnsConstants.MulticastHopLimit);
            socket.MulticastLoopback = true;

            var joined = 0;
            foreach (var index in MdnsNetworkInterfaces.Ipv6InterfaceIndexes(includeLoopback: true))
            {
                try
                {
                    socket.JoinMulticastGroup(index, MdnsConstants.GroupIpv6);
                    joined++;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "IPv6 mDNS cannot join on interface {Index}", index);
                }
            }
            if (joined == 0) socket.JoinMulticastGroup(MdnsConstants.GroupIpv6);
            return socket;
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException or NotSupportedException)
        {
            _logger.LogWarning(ex, "IPv6 mDNS is unavailable; connect by IP address instead");
            socket?.Dispose();
            return null;
        }
    }

    /// <summary>Called while the peer's own lock may be held. It never performs socket I/O.</summary>
    public void SetHostPort(int? port, string? nickname = null)
    {
        if (nickname is not null && !PlayerNickname.IsValid(nickname))
            throw new ArgumentException("Invalid nickname", nameof(nickname));
        lock (_gate)
        {
            if (_disposed || _desiredPort == port && (nickname is null || nickname == _desiredNickname)) return;
            _desiredPort = port;
            if (nickname is not null) _desiredNickname = nickname;
        }
        if (_advertiseSignal.CurrentCount == 0)
            try { _advertiseSignal.Release(); }
            catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException) { }
    }

    public async Task<IReadOnlyList<DiscoveredHost>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (_ipv4Socket is null && _ipv6Socket is null) return [];
        var channel = Channel.CreateBounded<MdnsDatagram>(new BoundedChannelOptions(MdnsConstants.ListenerCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        lock (_gate)
        {
            if (_disposed) return [];
            _listeners.Add(channel);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeout.CancelAfter(MdnsConstants.DiscoveryResponseWindow);
            await SendMulticastAsync(MdnsPacketCodec.Query(MdnsConstants.ServiceType, MdnsPacketCodec.Ptr), timeout.Token);
            var session = new MdnsQuerySession(MdnsConstants.ServiceType, FullInstanceName,
                MdnsNetworkInterfaces.IsOnLocalSubnet);

            while (!timeout.IsCancellationRequested)
            {
                MdnsDatagram datagram;
                try { datagram = await channel.Reader.ReadAsync(timeout.Token); }
                catch (OperationCanceledException) { break; }

                foreach (var record in datagram.Message.Records)
                    foreach (var (name, type) in session.Observe(record, datagram.Source))
                        await SendMulticastAsync(MdnsPacketCodec.Query(name, type), timeout.Token);
            }

            return session.GetResults();
        }
        finally
        {
            lock (_gate) _listeners.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    private async Task ReceiveAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try { datagram = await socket.ReceiveAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "mDNS receive failed");
                try { await Task.Delay(NetworkConstants.UdpReceiveRetryDelay, cancellationToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            if (!MdnsPacketCodec.TryParse(datagram.Buffer, out var message)) continue;

            if (message.IsResponse)
            {
                // RFC 6762 replies from another port are not mDNS answers.
                if (datagram.RemoteEndPoint.Port != MdnsConstants.Port) continue;
                lock (_gate)
                    foreach (var listener in _listeners)
                        listener.Writer.TryWrite(new MdnsDatagram(message, datagram.RemoteEndPoint.Address));
                continue;
            }

            int? port;
            string nickname;
            lock (_gate)
            {
                port = _desiredPort;
                nickname = _desiredNickname;
            }
            if (port is null) continue;
            var matchingQuestions = message.Questions.Where(q =>
                    (q.Type is MdnsPacketCodec.Ptr or MdnsPacketCodec.Any && SameName(q.Name, MdnsConstants.ServiceType)) ||
                    (q.Type is MdnsPacketCodec.Srv or MdnsPacketCodec.Txt or MdnsPacketCodec.Any && SameName(q.Name, FullInstanceName)) ||
                    (q.Type is MdnsPacketCodec.A or MdnsPacketCodec.Aaaa or MdnsPacketCodec.Any &&
                     SameName(q.Name, HostName))).ToArray();
            if (matchingQuestions.Length == 0) continue;

            var legacy = datagram.RemoteEndPoint.Port != MdnsConstants.Port;
            var unicast = legacy || matchingQuestions.Any(q => q.UnicastResponse);
            var response = MdnsPacketCodec.Advertisement(MdnsConstants.ServiceType, FullInstanceName,
                HostName, port.Value, AdvertisedAddresses(),
                legacy ? MdnsConstants.LegacyResponseTtl : MdnsConstants.ServiceRecordTtl,
                nickname, legacy ? message.Id : (ushort)0, legacy ? message.Questions : null, legacy);
            if (unicast)
                await SendUnicastAsync(socket, response, datagram.RemoteEndPoint, cancellationToken);
            else
                await SendMulticastAsync(response, cancellationToken);
        }
    }

    private async Task AdvertiseAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await _advertiseSignal.WaitAsync(MdnsConstants.AnnouncementInterval, cancellationToken); }
            catch (OperationCanceledException) { break; }

            int? desired;
            string desiredNickname;
            lock (_gate)
            {
                desired = _desiredPort;
                desiredNickname = _desiredNickname;
            }
            if (_publishedPort is { } oldPort &&
                (oldPort != desired || _publishedNickname != desiredNickname))
            {
                await SendMulticastAsync(MdnsPacketCodec.Advertisement(MdnsConstants.ServiceType, FullInstanceName,
                    HostName, oldPort, AdvertisedAddresses(),
                    MdnsConstants.GoodbyeTtl, _publishedNickname!), cancellationToken);
                _publishedPort = null;
                _publishedNickname = null;
            }
            if (desired is { } newPort)
            {
                await SendMulticastAsync(MdnsPacketCodec.Advertisement(MdnsConstants.ServiceType, FullInstanceName,
                    HostName, newPort, AdvertisedAddresses(),
                    MdnsConstants.ServiceRecordTtl, desiredNickname), cancellationToken);
                _publishedPort = newPort;
                _publishedNickname = desiredNickname;
            }
        }
    }

    private async Task SendMulticastAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_ipv4Socket is null && _ipv6Socket is null) return;
        try { await _sendGate.WaitAsync(cancellationToken); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
        try
        {
            if (_ipv4Socket is not null)
            {
                var addresses = MdnsNetworkInterfaces.LocalInterfaceAddresses(includeLoopback: true);
                foreach (var address in addresses)
                {
                    try
                    {
                        _ipv4Socket.Client.SetSocketOption(SocketOptionLevel.IP,
                            SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        await _ipv4Socket.SendAsync(packet, MdnsConstants.GroupEndpoint, cancellationToken);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        _logger.LogDebug(ex, "IPv4 mDNS send failed on {Address}", address);
                    }
                }
            }
            if (_ipv6Socket is not null)
                foreach (var index in MdnsNetworkInterfaces.Ipv6InterfaceIndexes(includeLoopback: true)
                             .DefaultIfEmpty(0))
                {
                    try
                    {
                        // The multicast destination's scope selects the outgoing link.
                        var destination = index > 0
                            ? new IPEndPoint(new IPAddress(MdnsConstants.GroupIpv6.GetAddressBytes(), index),
                                MdnsConstants.Port)
                            : MdnsConstants.GroupIpv6Endpoint;
                        await _ipv6Socket.SendAsync(packet, destination, cancellationToken);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        _logger.LogDebug(ex, "IPv6 mDNS send failed on interface {Index}", index);
                    }
                }
        }
        finally { _sendGate.Release(); }
    }

    private async Task SendUnicastAsync(UdpClient socket, byte[] packet, IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        try { await _sendGate.WaitAsync(cancellationToken); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
        try
        {
            try { await socket.SendAsync(packet, endpoint, cancellationToken); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "mDNS unicast reply failed to {Endpoint}", endpoint);
            }
        }
        finally { _sendGate.Release(); }
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static IPAddress[] AdvertisedAddresses()
    {
        var addresses = MdnsNetworkInterfaces.LocalInterfaceAddresses(includeLoopback: false)
            .Concat(MdnsNetworkInterfaces.LocalInterfaceAddresses(
                includeLoopback: false, family: AddressFamily.InterNetworkV6))
            .Where(address => !IPAddress.IsLoopback(address))
            .Distinct()
            .ToArray();
        return addresses.Length > 0 ? addresses : [IPAddress.Loopback, IPAddress.IPv6Loopback];
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _desiredPort = null;
        }
        var lastPublishedPort = _publishedPort;
        var lastPublishedNickname = _publishedNickname;
        _stop.Cancel();
        try { await Task.WhenAll([.. _receiveTasks, _advertiseTask]); }
        catch (OperationCanceledException) { }
        // A cancelled worker may have abandoned its goodbye after clearing the
        // published state. Repeat it for both the prior and final ports.
        foreach (var port in new int?[] { lastPublishedPort, _publishedPort }
                     .Where(value => value is not null).Select(value => value!.Value).Distinct())
            await SendMulticastAsync(MdnsPacketCodec.Advertisement(MdnsConstants.ServiceType, FullInstanceName,
                HostName, port, AdvertisedAddresses(),
                MdnsConstants.GoodbyeTtl, lastPublishedNickname ?? _publishedNickname ?? _desiredNickname), CancellationToken.None);
        _ipv4Socket?.Dispose();
        _ipv6Socket?.Dispose();
        _advertiseSignal.Dispose();
        _sendGate.Dispose();
        _stop.Dispose();
    }

    private sealed record MdnsDatagram(MdnsPacketCodec.Message Message, IPAddress Source);
}
