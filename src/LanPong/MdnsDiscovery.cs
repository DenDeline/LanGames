using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;

namespace LanPong;

/// <summary>IPv4 DNS-SD for games waiting on the local link.</summary>
internal sealed class MdnsDiscovery : IAsyncDisposable
{
    private readonly string _instanceName = $"LanPong-{Guid.NewGuid():N}";
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _advertiseSignal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Channel<MdnsDatagram>> _listeners = [];
    private readonly UdpClient? _socket;
    private readonly Task _receiveTask;
    private readonly Task _advertiseTask;
    private int? _desiredPort;
    private int? _publishedPort;
    private bool _disposed;

    private string FullInstanceName => $"{_instanceName}.{MdnsConstants.ServiceType}";
    private string HostName => $"{_instanceName.ToLowerInvariant()}.local.";

    public MdnsDiscovery(ILogger logger)
    {
        _logger = logger;
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
            _socket = socket;
            _receiveTask = ReceiveAsync(_stop.Token);
            _advertiseTask = AdvertiseAsync(_stop.Token);
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException)
        {
            _logger.LogWarning(ex, "mDNS is unavailable; connect by IP address instead");
            socket?.Dispose();
            _receiveTask = Task.CompletedTask;
            _advertiseTask = Task.CompletedTask;
        }
    }

    /// <summary>Called while the peer's own lock may be held. It never performs socket I/O.</summary>
    public void SetHostPort(int? port)
    {
        lock (_gate)
        {
            if (_disposed || _desiredPort == port) return;
            _desiredPort = port;
        }
        if (_advertiseSignal.CurrentCount == 0)
            try { _advertiseSignal.Release(); }
            catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException) { }
    }

    public async Task<IReadOnlyList<DiscoveredHost>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (_socket is null) return [];
        var channel = Channel.CreateBounded<MdnsDatagram>(new BoundedChannelOptions(MdnsConstants.ListenerCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
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

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        var socket = _socket!;
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
            lock (_gate) port = _desiredPort;
            if (port is null) continue;
            var matchingQuestions = message.Questions.Where(q =>
                    (q.Type is MdnsPacketCodec.Ptr or MdnsPacketCodec.Any && SameName(q.Name, MdnsConstants.ServiceType)) ||
                    (q.Type is MdnsPacketCodec.Srv or MdnsPacketCodec.Txt or MdnsPacketCodec.Any && SameName(q.Name, FullInstanceName)) ||
                    (q.Type is MdnsPacketCodec.A or MdnsPacketCodec.Any && SameName(q.Name, HostName))).ToArray();
            if (matchingQuestions.Length == 0) continue;

            var legacy = datagram.RemoteEndPoint.Port != MdnsConstants.Port;
            var unicast = legacy || matchingQuestions.Any(q => q.UnicastResponse);
            var response = MdnsPacketCodec.Advertisement(MdnsConstants.ServiceType, FullInstanceName,
                HostName, port.Value, MdnsNetworkInterfaces.LocalInterfaceAddresses(includeLoopback: false),
                legacy ? MdnsConstants.LegacyResponseTtl : MdnsConstants.ServiceRecordTtl,
                legacy ? message.Id : (ushort)0, legacy ? message.Questions : null, legacy);
            if (unicast)
                await SendUnicastAsync(response, datagram.RemoteEndPoint, cancellationToken);
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
            lock (_gate) desired = _desiredPort;
            if (_publishedPort is { } oldPort && oldPort != desired)
            {
                await SendMulticastAsync(MdnsPacketCodec.Advertisement(MdnsConstants.ServiceType, FullInstanceName,
                    HostName, oldPort, MdnsNetworkInterfaces.LocalInterfaceAddresses(includeLoopback: false),
                    MdnsConstants.GoodbyeTtl), cancellationToken);
                _publishedPort = null;
            }
            if (desired is { } newPort)
            {
                await SendMulticastAsync(MdnsPacketCodec.Advertisement(MdnsConstants.ServiceType, FullInstanceName,
                    HostName, newPort, MdnsNetworkInterfaces.LocalInterfaceAddresses(includeLoopback: false),
                    MdnsConstants.ServiceRecordTtl), cancellationToken);
                _publishedPort = newPort;
            }
        }
    }

    private async Task SendMulticastAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_socket is null) return;
        try { await _sendGate.WaitAsync(cancellationToken); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
        try
        {
            var addresses = MdnsNetworkInterfaces.LocalInterfaceAddresses(includeLoopback: true);
            foreach (var address in addresses)
            {
                try
                {
                    _socket.Client.SetSocketOption(SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    await _socket.SendAsync(packet, MdnsConstants.GroupEndpoint, cancellationToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    _logger.LogDebug(ex, "mDNS send failed on {Address}", address);
                }
            }
        }
        finally { _sendGate.Release(); }
    }

    private async Task SendUnicastAsync(byte[] packet, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        if (_socket is null) return;
        try { await _sendGate.WaitAsync(cancellationToken); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
        try
        {
            try { await _socket.SendAsync(packet, endpoint, cancellationToken); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "mDNS unicast reply failed to {Endpoint}", endpoint);
            }
        }
        finally { _sendGate.Release(); }
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _desiredPort = null;
        }
        var lastPublishedPort = _publishedPort;
        _stop.Cancel();
        try { await Task.WhenAll(_receiveTask, _advertiseTask); }
        catch (OperationCanceledException) { }
        // A cancelled worker may have abandoned its goodbye after clearing the
        // published state. Repeat it for both the prior and final ports.
        foreach (var port in new int?[] { lastPublishedPort, _publishedPort }
                     .Where(value => value is not null).Select(value => value!.Value).Distinct())
            await SendMulticastAsync(MdnsPacketCodec.Advertisement(MdnsConstants.ServiceType, FullInstanceName,
                HostName, port, MdnsNetworkInterfaces.LocalInterfaceAddresses(includeLoopback: false),
                MdnsConstants.GoodbyeTtl), CancellationToken.None);
        _socket?.Dispose();
        _advertiseSignal.Dispose();
        _sendGate.Dispose();
        _stop.Dispose();
    }

    private sealed record MdnsDatagram(MdnsPacketCodec.Message Message, IPAddress Source);
}
