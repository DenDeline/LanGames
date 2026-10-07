using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanPong;

internal sealed partial class PongPeer
{
    private Task StartReceiving(UdpClient socket, CancellationTokenSource stop)
    {
        // A leave may dispose UdpClient before the scheduled receive task starts.
        // Capture the underlying socket and its address family while both are live.
        var receiveSocket = socket.Client;
        var anyEndpoint = receiveSocket.AddressFamily == AddressFamily.InterNetworkV6
            ? NetworkConstants.AnyIpv6Endpoint
            : NetworkConstants.AnyIpv4Endpoint;
        return Task.Run(() => ReceiveAsync(socket, receiveSocket, anyEndpoint, stop));
    }

    private async Task ReceiveAsync(UdpClient socket, Socket receiveSocket,
        IPEndPoint anyEndpoint, CancellationTokenSource stop)
    {
        using var ownedStop = stop;
        var cancellationToken = ownedStop.Token;
        // One receive is outstanding at a time, so the datagram and remote address
        // can be handled before the next receive overwrites either buffer.
        var receiveBuffer = new byte[WirePacketCodec.MaxPacketBytes + 1];
        var receiveFrom = anyEndpoint.Serialize();
        var replyBuffer = new ArrayBufferWriter<byte>();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var receivedBytes = await receiveSocket.ReceiveFromAsync(
                    receiveBuffer.AsMemory(), SocketFlags.None, receiveFrom, cancellationToken);
                if (receivedBytes > WirePacketCodec.MaxPacketBytes || !MayReceiveFrom(socket, receiveFrom)) continue;
                if (!WirePacketCodec.TryDeserialize(receiveBuffer.AsMemory(0, receivedBytes), out var packet))
                    continue;
                await HandlePacketAsync(socket, anyEndpoint, receiveFrom, packet, cancellationToken, replyBuffer);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                // Windows reports an oversized datagram as MessageSize rather than truncating it.
                continue;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "UDP receive failed");
                try { await Task.Delay(NetworkConstants.UdpReceiveRetryDelay, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private bool MayReceiveFrom(UdpClient socket, SocketAddress remote)
    {
        lock (_gate)
        {
            if (_socket != socket) return false;
            return _role switch
            {
                PeerRole.Host => _peerSocketAddress is null || _peerSocketAddress.Equals(remote),
                PeerRole.Guest => _targetSocketAddress?.Equals(remote) == true,
                _ => false
            };
        }
    }

    private async Task SendQuietlyAsync(
        UdpClient socket, IPEndPoint destination, WirePacket packet, CancellationToken cancellationToken,
        ArrayBufferWriter<byte>? sendBuffer = null)
    {
        try
        {
            if (sendBuffer is null)
            {
                var bytes = WirePacketCodec.Serialize(packet);
                await socket.SendAsync(bytes, destination, cancellationToken);
            }
            else
            {
                WirePacketCodec.Serialize(packet, sendBuffer);
                await socket.SendAsync(sendBuffer.WrittenMemory, destination, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex) { _logger.LogDebug(ex, "UDP send failed to {Destination}", destination); }
        finally { sendBuffer?.Clear(); }
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Порт должен быть от 1 до 65535.");
    }

    private static UdpClient CreateUdpSocket(int port)
    {
        if (!Socket.OSSupportsIPv6)
            return new UdpClient(new IPEndPoint(IPAddress.Any, port));

        var socket = new UdpClient(AddressFamily.InterNetworkV6);
        try
        {
            // Set this before binding so one socket can receive both address families.
            socket.Client.DualMode = true;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }

        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string? FormatEndpoint(IPEndPoint? endpoint)
    {
        if (endpoint is null) return null;
        return endpoint.Address.IsIPv4MappedToIPv6
            ? $"{endpoint.Address.MapToIPv4()}:{endpoint.Port}"
            : endpoint.ToString();
    }

    private static string[] GetLocalAddresses()
    {
        string[] loopbacks = Socket.OSSupportsIPv6 ? ["127.0.0.1", "::1"] : ["127.0.0.1"];
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => (a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) &&
                            !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct()
                .Concat(loopbacks)
                .ToArray();
        }
        catch (NetworkInformationException) { return loopbacks; }
    }
}
