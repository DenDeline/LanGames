using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace LanPong;

/// <summary>IPv4 DNS-SD for games waiting on the local link.</summary>
internal sealed class MdnsDiscovery : IAsyncDisposable
{
    private const string ServiceType = "_lanpong._udp.local.";
    private const int MdnsPort = 5353;
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint GroupEndpoint = new(Group, MdnsPort);
    private static readonly TimeSpan AnnouncementInterval = TimeSpan.FromSeconds(15);
    private readonly string _instanceName = $"LanPong-{Guid.NewGuid():N}";
    private readonly ILogger _logger;
    private readonly object _gate = new();
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

    private string FullInstanceName => $"{_instanceName}.{ServiceType}";
    private string HostName => $"{_instanceName.ToLowerInvariant()}.local.";

    public MdnsDiscovery(ILogger logger)
    {
        _logger = logger;
        UdpClient? socket = null;
        try
        {
            socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.MulticastLoopback = true;

            var joined = 0;
            foreach (var address in LocalInterfaceAddresses(includeLoopback: true))
            {
                try
                {
                    socket.JoinMulticastGroup(Group, address);
                    joined++;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "mDNS cannot join on {Address}", address);
                }
            }
            if (joined == 0) socket.JoinMulticastGroup(Group);
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
        var channel = Channel.CreateBounded<MdnsDatagram>(new BoundedChannelOptions(64)
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
            timeout.CancelAfter(NetworkConstants.DiscoveryResponseWindow);
            await SendMulticastAsync(MdnsPacketCodec.Query(ServiceType, MdnsPacketCodec.Ptr), timeout.Token);
            var instances = new Dictionary<string, FoundInstance>(StringComparer.OrdinalIgnoreCase);
            var withdrawn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addresses = new Dictionary<string, List<(IPAddress Address, IPAddress Source)>>(StringComparer.OrdinalIgnoreCase);
            var queried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!timeout.IsCancellationRequested)
            {
                MdnsDatagram datagram;
                try { datagram = await channel.Reader.ReadAsync(timeout.Token); }
                catch (OperationCanceledException) { break; }

                var message = datagram.Message;
                foreach (var record in message.Records)
                {
                    if (record.Type == MdnsPacketCodec.Ptr && SameName(record.Name, ServiceType) &&
                        record.Ttl == 0 && record.Target is { } retiredInstance)
                    {
                        instances.Remove(retiredInstance);
                        withdrawn.Add(retiredInstance);
                        continue;
                    }
                    if (record.Ttl == 0) continue;
                    if (record.Type == MdnsPacketCodec.Ptr && SameName(record.Name, ServiceType) &&
                        record.Target is { } instanceName)
                    {
                        withdrawn.Remove(instanceName);
                        if (!instances.TryGetValue(instanceName, out var instance))
                            instances[instanceName] = instance = new FoundInstance();
                        instance.SeenPtr = true;
                        if (queried.Add(instanceName))
                        {
                            // Some responders omit SRV/TXT/A from the PTR response.
                            await SendMulticastAsync(MdnsPacketCodec.Query(instanceName, MdnsPacketCodec.Srv), timeout.Token);
                            await SendMulticastAsync(MdnsPacketCodec.Query(instanceName, MdnsPacketCodec.Txt), timeout.Token);
                        }
                    }
                    else if (record.Type == MdnsPacketCodec.Srv && record.Target is { } target &&
                             record.Name.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase) &&
                             !withdrawn.Contains(record.Name))
                    {
                        if (!instances.TryGetValue(record.Name, out var instance))
                            instances[record.Name] = instance = new FoundInstance();
                        instance.Port = record.Port;
                        instance.Target = target;
                        if (queried.Add(target))
                            await SendMulticastAsync(MdnsPacketCodec.Query(target, MdnsPacketCodec.A), timeout.Token);
                    }
                    else if (record.Type == MdnsPacketCodec.Txt &&
                             record.Name.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase) &&
                             !withdrawn.Contains(record.Name))
                    {
                        if (!instances.TryGetValue(record.Name, out var instance))
                            instances[record.Name] = instance = new FoundInstance();
                        instance.Version = record.Version;
                    }
                    else if (record.Type == MdnsPacketCodec.A && record.Address is { } ip)
                    {
                        if (!addresses.TryGetValue(record.Name, out var candidates))
                            addresses[record.Name] = candidates = [];
                        if (!candidates.Any(item => item.Address.Equals(ip) && item.Source.Equals(datagram.Source)))
                            candidates.Add((ip, datagram.Source));
                    }
                }
            }

            var results = new Dictionary<string, DiscoveredHost>(StringComparer.Ordinal);
            foreach (var (name, instance) in instances)
            {
                if (!instance.SeenPtr || withdrawn.Contains(name) || SameName(name, FullInstanceName) ||
                    instance.Version != WirePacket.CurrentVersion ||
                    instance.Port is < 1 or > 65535 || instance.Target is null ||
                    !addresses.TryGetValue(instance.Target, out var candidates)) continue;

                // A multi-homed host may advertise several addresses. The A record
                // that matches the response's source is on the observed local link.
                var chosen = candidates.FirstOrDefault(item => item.Address.Equals(item.Source)).Address
                    ?? candidates.Select(item => item.Address).FirstOrDefault(IsOnLocalSubnet);
                if (chosen is null || chosen.Equals(IPAddress.Any)) continue;
                var host = new DiscoveredHost(chosen.ToString(), instance.Port);
                results[$"{host.Address}:{host.Port}"] = host;
            }
            return results.Values.ToArray();
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
                if (datagram.RemoteEndPoint.Port != MdnsPort) continue;
                lock (_gate)
                    foreach (var listener in _listeners)
                        listener.Writer.TryWrite(new MdnsDatagram(message, datagram.RemoteEndPoint.Address));
                continue;
            }

            int? port;
            lock (_gate) port = _desiredPort;
            if (port is null) continue;
            var matchingQuestions = message.Questions.Where(q =>
                    (q.Type is MdnsPacketCodec.Ptr or MdnsPacketCodec.Any && SameName(q.Name, ServiceType)) ||
                    (q.Type is MdnsPacketCodec.Srv or MdnsPacketCodec.Txt or MdnsPacketCodec.Any && SameName(q.Name, FullInstanceName)) ||
                    (q.Type is MdnsPacketCodec.A or MdnsPacketCodec.Any && SameName(q.Name, HostName))).ToArray();
            if (matchingQuestions.Length == 0) continue;

            var legacy = datagram.RemoteEndPoint.Port != MdnsPort;
            var unicast = legacy || matchingQuestions.Any(q => q.UnicastResponse);
            var response = MdnsPacketCodec.Advertisement(ServiceType, FullInstanceName,
                HostName, port.Value, LocalInterfaceAddresses(includeLoopback: false), legacy ? 10u : 120u,
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
            try { await _advertiseSignal.WaitAsync(AnnouncementInterval, cancellationToken); }
            catch (OperationCanceledException) { break; }

            int? desired;
            lock (_gate) desired = _desiredPort;
            if (_publishedPort is { } oldPort && oldPort != desired)
            {
                await SendMulticastAsync(MdnsPacketCodec.Advertisement(ServiceType, FullInstanceName,
                    HostName, oldPort, LocalInterfaceAddresses(includeLoopback: false), 0), cancellationToken);
                _publishedPort = null;
            }
            if (desired is { } newPort)
            {
                await SendMulticastAsync(MdnsPacketCodec.Advertisement(ServiceType, FullInstanceName,
                    HostName, newPort, LocalInterfaceAddresses(includeLoopback: false), 120), cancellationToken);
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
            var addresses = LocalInterfaceAddresses(includeLoopback: true);
            foreach (var address in addresses)
            {
                try
                {
                    _socket.Client.SetSocketOption(SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    await _socket.SendAsync(packet, GroupEndpoint, cancellationToken);
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

    private static IPAddress[] LocalInterfaceAddresses(bool includeLoopback)
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
            if (addresses.Length == 0 && includeLoopback) return [IPAddress.Loopback];
            if (addresses.Length == 0) return [IPAddress.Loopback];
            return addresses;
        }
        catch (NetworkInformationException) { return [IPAddress.Loopback]; }
    }

    private static bool IsOnLocalSubnet(IPAddress address)
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
                if (Enumerable.Range(0, 4).All(i => (candidate[i] & mask[i]) == (own[i] & mask[i])))
                    return true;
            }
        }
        catch (NetworkInformationException) { }
        return false;
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
            await SendMulticastAsync(MdnsPacketCodec.Advertisement(ServiceType, FullInstanceName,
                HostName, port, LocalInterfaceAddresses(includeLoopback: false), 0), CancellationToken.None);
        _socket?.Dispose();
        _advertiseSignal.Dispose();
        _sendGate.Dispose();
        _stop.Dispose();
    }

    private sealed class FoundInstance
    {
        public bool SeenPtr { get; set; }
        public int Port { get; set; }
        public string? Target { get; set; }
        public int? Version { get; set; }
    }

    private sealed record MdnsDatagram(MdnsPacketCodec.Message Message, IPAddress Source);
}

/// <summary>Small bounded DNS packet codec for the four DNS-SD record types used by LAN Pong.</summary>
internal static class MdnsPacketCodec
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Srv = 33;
    public const ushort Any = 255;
    private const int MaxPacketBytes = 9000;
    private const int MaxRecords = 64;

    public sealed record Question(string Name, ushort Type, ushort Class = 1)
    {
        public bool UnicastResponse => (Class & 0x8000) != 0;
    }
    public sealed record Record(string Name, ushort Type, uint Ttl, string? Target = null,
        int Port = 0, int? Version = null, IPAddress? Address = null);
    public sealed record Message(ushort Id, bool IsResponse, IReadOnlyList<Question> Questions,
        IReadOnlyList<Record> Records);

    public static byte[] Query(string name, ushort type)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, 0, response: false, questionCount: 1, answerCount: 0);
        WriteName(stream, name);
        WriteU16(stream, type);
        WriteU16(stream, 1);
        return stream.ToArray();
    }

    public static byte[] Advertisement(string serviceType, string instanceName, string hostName,
        int port, IReadOnlyList<IPAddress> addresses, uint ttl, ushort id = 0,
        IReadOnlyList<Question>? questions = null, bool legacy = false)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, id, response: true, questionCount: checked((ushort)(questions?.Count ?? 0)),
            answerCount: checked((ushort)(3 + addresses.Count)));
        if (questions is not null)
            foreach (var question in questions)
            {
                WriteName(stream, question.Name);
                WriteU16(stream, question.Type);
                WriteU16(stream, (ushort)(question.Class & 0x7fff));
            }
        WriteRecord(stream, serviceType, Ptr, ttl, data => WriteName(data, instanceName), unique: false);
        WriteRecord(stream, instanceName, Srv, ttl, data =>
        {
            WriteU16(data, 0);
            WriteU16(data, 0);
            WriteU16(data, checked((ushort)port));
            WriteName(data, hostName);
        }, unique: !legacy);
        WriteRecord(stream, instanceName, Txt, ttl, data =>
        {
            var property = Encoding.ASCII.GetBytes($"version={WirePacket.CurrentVersion}");
            data.WriteByte(checked((byte)property.Length));
            data.Write(property);
        }, unique: !legacy);
        foreach (var address in addresses)
            WriteRecord(stream, hostName, A, ttl, data => data.Write(address.GetAddressBytes()), unique: !legacy);
        return stream.ToArray();
    }

    public static bool TryParse(byte[] bytes, out Message message)
    {
        message = null!;
        if (bytes.Length is < 12 or > MaxPacketBytes) return false;
        var span = bytes.AsSpan();
        var id = BinaryPrimitives.ReadUInt16BigEndian(span);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        if ((flags & 0x7800) != 0) return false; // Standard DNS opcode only.
        var questionsCount = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var answersCount = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);
        var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(span[10..]);
        if (questionsCount + answersCount + authorityCount + additionalCount > MaxRecords) return false;
        var offset = 12;
        var questions = new List<Question>(questionsCount);
        var records = new List<Record>(answersCount + authorityCount + additionalCount);

        for (var i = 0; i < questionsCount; i++)
        {
            if (!ReadName(bytes, ref offset, out var name) || offset + 4 > bytes.Length) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            var qclass = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 2)..]);
            offset += 4;
            questions.Add(new Question(name, type, qclass));
        }
        var count = answersCount + authorityCount + additionalCount;
        for (var i = 0; i < count; i++)
        {
            if (!ReadName(bytes, ref offset, out var name) || offset + 10 > bytes.Length) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 4)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 8)..]);
            offset += 10;
            if (offset + length > bytes.Length) return false;
            var end = offset + length;
            string? target = null;
            var port = 0;
            int? version = null;
            IPAddress? address = null;
            switch (type)
            {
                case Ptr:
                    if (!ReadName(bytes, ref offset, out target) || offset != end) return false;
                    break;
                case Srv:
                    if (length < 7) return false;
                    port = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 4)..]);
                    offset += 6;
                    if (!ReadName(bytes, ref offset, out target) || offset != end) return false;
                    break;
                case Txt:
                    while (offset < end)
                    {
                        var itemLength = bytes[offset++];
                        if (offset + itemLength > end) return false;
                        var item = Encoding.UTF8.GetString(bytes, offset, itemLength);
                        if (item.StartsWith("version=", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(item.AsSpan(8), out var parsed)) version = parsed;
                        offset += itemLength;
                    }
                    break;
                case A when length == 4:
                    address = new IPAddress(span.Slice(offset, 4));
                    break;
            }
            offset = end;
            records.Add(new Record(name, type, ttl, target, port, version, address));
        }
        message = new Message(id, (flags & 0x8000) != 0, questions, records);
        return true;
    }

    private static bool ReadName(byte[] bytes, ref int offset, out string name)
    {
        name = string.Empty;
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;
        var totalLength = 0;
        while (cursor < bytes.Length && jumps++ < 32)
        {
            var length = bytes[cursor++];
            if (length == 0)
            {
                if (!jumped) offset = cursor;
                name = string.Join('.', labels) + ".";
                return true;
            }
            if ((length & 0xc0) == 0xc0)
            {
                if (cursor >= bytes.Length) return false;
                var pointer = ((length & 0x3f) << 8) | bytes[cursor++];
                if (pointer >= bytes.Length || pointer >= cursor - 2) return false;
                if (!jumped) offset = cursor;
                cursor = pointer;
                jumped = true;
                continue;
            }
            if ((length & 0xc0) != 0 || length > 63 || cursor + length > bytes.Length) return false;
            totalLength += length + 1;
            if (totalLength > 254 || labels.Count >= 127) return false;
            labels.Add(Encoding.UTF8.GetString(bytes, cursor, length));
            cursor += length;
        }
        return false;
    }

    private static void WriteHeader(Stream stream, ushort id, bool response, ushort questionCount,
        ushort answerCount)
    {
        WriteU16(stream, id);
        WriteU16(stream, response ? (ushort)0x8400 : (ushort)0);
        WriteU16(stream, questionCount);
        WriteU16(stream, answerCount);
        WriteU16(stream, 0);
        WriteU16(stream, 0);
    }

    private static void WriteRecord(Stream stream, string name, ushort type, uint ttl,
        Action<Stream> writeData, bool unique)
    {
        using var data = new MemoryStream();
        writeData(data);
        WriteName(stream, name);
        WriteU16(stream, type);
        WriteU16(stream, unique ? (ushort)0x8001 : (ushort)1);
        WriteU32(stream, ttl);
        WriteU16(stream, checked((ushort)data.Length));
        data.Position = 0;
        data.CopyTo(stream);
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is < 1 or > 63) throw new ArgumentException("Invalid DNS name", nameof(name));
            stream.WriteByte(checked((byte)bytes.Length));
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static void WriteU16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
