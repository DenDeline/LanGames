using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanPong;

/// <summary>One local player and one remote player, connected directly over UDP.</summary>
internal sealed class PongPeer : IHostedLifecycleService, IAsyncDisposable
{
    private readonly ILogger<PongPeer> _logger;
    private readonly Lock _gate = new();
    private readonly Lock _shutdownGate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GameEngine _game = new();
    private readonly Dictionary<Guid, (int Axis, DateTime Updated)> _controllers = [];
    private readonly string[] _localAddresses = GetLocalAddresses();
    private readonly Task _clockTask;
    private Task? _shutdownTask;
    private bool _stopping;
    private int _disposed;

    private UdpClient? _socket;
    private CancellationTokenSource? _socketStop;
    private Task? _receiveTask;
    private IPEndPoint? _peerEndpoint;
    private IPEndPoint? _targetEndpoint;
    private SocketAddress? _peerSocketAddress;
    private SocketAddress? _targetSocketAddress;
    private string? _sessionId;
    private string? _lastRestartRequestId;
    private string? _pendingRestartRequestId;
    private int _restartAfterRound;
    private PeerRole _role = PeerRole.None;
    private ConnectionState _connection = ConnectionState.Idle;
    private string _message = "Создайте игру или подключитесь к другу.";
    private int _udpPort;
    private int _remoteAxis;
    private long _outSequence;
    private long _nextInputSendTimestamp;
    private int _lastSentAxis;
    private long _lastInputSequence = -1;
    private long _lastStateSequence = -1;
    private long _lastStateSentTick;
    private long _pingSequence;
    private long _pendingPingSequence;
    private long _pingSentTimestamp;
    private double? _pingMs;
    private DateTime _lastPeerSeen = DateTime.MinValue;
    private DateTime _lastInputSeen = DateTime.MinValue;
    private DateTime _lastHelloSent = DateTime.MinValue;
    private DateTime _lastRestartSent = DateTime.MinValue;
    private DateTime _lastPingSent = DateTime.MinValue;
    private DateTime _lastPongSeen = DateTime.MinValue;

    public PongPeer(ILogger<PongPeer> logger)
    {
        _logger = logger;
        _clockTask = Task.Run(() => ClockAsync(_lifetime.Token));
    }

    public PongSnapshot Snapshot()
    {
        lock (_gate)
        {
            var state = _game.Capture();
            return new PongSnapshot(
                _role, _connection, _message, _udpPort, _localAddresses,
                (_peerEndpoint ?? _targetEndpoint)?.ToString(),
                state.LeftY, state.RightY, state.BallX, state.BallY,
                state.BallVx, state.BallVy,
                state.LeftScore, state.RightScore, state.Phase,
                state.Countdown, state.TickNumber, state.RoundId, _pingMs);
        }
    }

    public void SetInput(Guid controllerId, int axis)
    {
        lock (_gate)
            if (!_stopping) _controllers[controllerId] = (Math.Clamp(axis, -1, 1), DateTime.UtcNow);
    }

    public void RemoveController(Guid controllerId)
    {
        lock (_gate) _controllers.Remove(controllerId);
    }

    public async Task HostAsync(int port)
    {
        ValidatePort(port);
        await _transition.WaitAsync();
        try
        {
            ThrowIfStopping();
            await StopSocketAsync();
            var socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            var stop = new CancellationTokenSource();
            lock (_gate)
            {
                if (_stopping)
                {
                    socket.Dispose();
                    stop.Dispose();
                    throw new InvalidOperationException("Приложение завершает работу.");
                }
                _socket = socket;
                _socketStop = stop;
                _role = PeerRole.Host;
                _connection = ConnectionState.Waiting;
                _message = "Ожидание второго игрока. Передайте ему ваш IP-адрес.";
                _udpPort = port;
            }
            _receiveTask = Task.Run(() => ReceiveAsync(socket, stop.Token));
        }
        finally { _transition.Release(); }
    }

    public async Task JoinAsync(string address, int port, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Введите IP-адрес создателя игры.");
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfStopping();
        var addresses = await Dns.GetHostAddressesAsync(address.Trim(), cancellationToken);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new ArgumentException("Нужен IPv4-адрес компьютера в локальной сети.");

        await _transition.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfStopping();
            await StopSocketAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var stop = new CancellationTokenSource();
            lock (_gate)
            {
                if (_stopping || cancellationToken.IsCancellationRequested)
                {
                    socket.Dispose();
                    stop.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("Приложение завершает работу.");
                }
                _socket = socket;
                _socketStop = stop;
                _targetEndpoint = new IPEndPoint(ip, port);
                _targetSocketAddress = _targetEndpoint.Serialize();
                _role = PeerRole.Guest;
                _connection = ConnectionState.Connecting;
                _message = "Подключаемся к игроку…";
                _udpPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            }
            _receiveTask = Task.Run(() => ReceiveAsync(socket, stop.Token));
        }
        finally { _transition.Release(); }
    }

    public void Restart()
    {
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("Приложение завершает работу.");
            if (_connection != ConnectionState.Connected) throw new InvalidOperationException("Сначала подключитесь к игре.");
            if (_role == PeerRole.Host) _game.StartMatch();
            else if (_role == PeerRole.Guest)
            {
                _pendingRestartRequestId = Guid.NewGuid().ToString("N");
                _restartAfterRound = _game.RoundId;
                _lastRestartSent = DateTime.MinValue;
            }
        }
    }

    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync();
        try
        {
            UdpClient? socket;
            IPEndPoint? peer;
            WirePacket? bye;
            lock (_gate)
            {
                socket = _socket;
                peer = _peerEndpoint ?? _targetEndpoint;
                bye = _sessionId is null ? null : new ByePacket { SessionId = _sessionId };
            }
            if (socket is not null && peer is not null && bye is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(NetworkConstants.UdpByeTimeout);
                await SendQuietlyAsync(socket, peer, bye, timeout.Token);
            }
            await StopSocketAsync();
        }
        finally { _transition.Release(); }
    }

    public async Task<IReadOnlyList<DiscoveredHost>> DiscoverAsync(int port, CancellationToken cancellationToken)
    {
        ValidatePort(port);
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        probe.EnableBroadcast = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NetworkConstants.DiscoveryResponseWindow);
        var query = WirePacketCodec.Serialize(new DiscoverPacket());
        var destinations = new[] { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var destination in destinations)
        {
            try { await probe.SendAsync(query, new IPEndPoint(destination, port), timeout.Token); }
            catch (SocketException ex) { _logger.LogDebug(ex, "Discovery send failed for {Address}", destination); }
        }

        var found = new Dictionary<string, DiscoveredHost>();
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var received = await probe.ReceiveAsync(timeout.Token);
                if (!WirePacketCodec.TryDeserialize(received.Buffer, out var packet)) continue;
                if (packet is not OfferPacket { Port: >= 1 and <= 65535 } offer) continue;
                var host = new DiscoveredHost(received.RemoteEndPoint.Address.ToString(), offer.Port);
                found[$"{host.Address}:{host.Port}"] = host;
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                // Some systems report ICMP "port unreachable" for the loopback probe here.
                _logger.LogDebug(ex, "Discovery receive failed");
                try { await Task.Delay(NetworkConstants.DiscoveryReceiveRetryDelay, timeout.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        return found.Values.ToArray();
    }

    private async Task StopSocketAsync()
    {
        UdpClient? socket;
        CancellationTokenSource? stop;
        Task? receiver;
        lock (_gate)
        {
            socket = _socket;
            stop = _socketStop;
            receiver = _receiveTask;
            _socket = null;
            _socketStop = null;
            _receiveTask = null;
            _peerEndpoint = _targetEndpoint = null;
            _peerSocketAddress = _targetSocketAddress = null;
            _sessionId = _lastRestartRequestId = _pendingRestartRequestId = null;
            _restartAfterRound = 0;
            _role = PeerRole.None;
            _connection = ConnectionState.Idle;
            _message = "Создайте игру или подключитесь к другу.";
            _udpPort = _remoteAxis = 0;
            _controllers.Clear();
            _outSequence = 0;
            _nextInputSendTimestamp = 0;
            _lastSentAxis = 0;
            _lastInputSequence = _lastStateSequence = -1;
            _lastStateSentTick = 0;
            _lastPeerSeen = _lastInputSeen = _lastHelloSent = _lastRestartSent = DateTime.MinValue;
            ResetPing();
            _game.ResetWaiting();
        }
        stop?.Cancel();
        socket?.Dispose();
        if (receiver is not null)
        {
            try { await receiver; }
            catch (OperationCanceledException) { }
        }
        stop?.Dispose();
    }

    private async Task ClockAsync(CancellationToken cancellationToken)
    {
        const double fixedStep = GameConstants.FixedStepSeconds;
        const int maxCatchUpSteps = NetworkConstants.MaximumSimulationCatchUpSteps;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(fixedStep));
        // Only this loop uses the buffer; each send completes before it is cleared and reused.
        var sendBuffer = new ArrayBufferWriter<byte>();
        var previousTimestamp = Stopwatch.GetTimestamp();
        var accumulatedTime = 0.0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var timestamp = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalSeconds;
                previousTimestamp = timestamp;
                UdpClient? socket;
                IPEndPoint? destination = null;
                WirePacket? packet = null;
                WirePacket? pingPacket = null;
                var now = DateTime.UtcNow;
                lock (_gate)
                {
                    socket = _socket;
                    if (socket is null)
                    {
                        accumulatedTime = 0;
                        continue;
                    }

                    if (_role == PeerRole.Host)
                    {
                        if (_peerEndpoint is not null && now - _lastPeerSeen > NetworkConstants.PeerIdleTimeout)
                        {
                            _peerEndpoint = null;
                            _peerSocketAddress = null;
                            _sessionId = null;
                            _connection = ConnectionState.Waiting;
                            _message = "Связь потеряна. Ожидание второго игрока…";
                            _remoteAxis = 0;
                            _lastStateSentTick = 0;
                            accumulatedTime = 0;
                            ResetPing();
                            _game.ResetWaiting();
                        }
                        if (_peerEndpoint is not null)
                        {
                            destination = _peerEndpoint;
                            var remoteAxis = now - _lastInputSeen < NetworkConstants.InputStaleAfter ? _remoteAxis : 0;
                            var localAxis = LocalAxis(now);
                            // PeriodicTimer coalesces missed wakes. Measure elapsed monotonic time
                            // and catch up a bounded number of fixed physics steps instead.
                            accumulatedTime = Math.Min(accumulatedTime + elapsed, fixedStep * maxCatchUpSteps);
                            for (var step = 0; step < maxCatchUpSteps && accumulatedTime >= fixedStep; step++)
                            {
                                _game.Advance(fixedStep, localAxis, remoteAxis);
                                accumulatedTime -= fixedStep;
                            }
                            if (_game.TickNumber - _lastStateSentTick >= NetworkConstants.StateSendIntervalTicks)
                            {
                                packet = CreateStatePacket();
                                _lastStateSentTick = _game.TickNumber;
                            }
                        }
                        else accumulatedTime = 0;
                    }
                    else if (_role == PeerRole.Guest && _targetEndpoint is not null)
                    {
                        accumulatedTime = 0;
                        destination = _targetEndpoint;
                        if (_connection == ConnectionState.Connected && now - _lastPeerSeen > NetworkConstants.PeerIdleTimeout)
                        {
                            _connection = ConnectionState.Connecting;
                            _message = "Связь потеряна. Повторное подключение…";
                            _sessionId = null;
                            _pendingRestartRequestId = null;
                            _restartAfterRound = 0;
                            ResetPing();
                            _game.ResetWaiting();
                        }
                        if (_connection == ConnectionState.Connecting && now - _lastHelloSent >= NetworkConstants.HelloRetryInterval)
                        {
                            _lastHelloSent = now;
                            packet = new HelloPacket();
                        }
                        else if (_connection == ConnectionState.Connected && _sessionId is not null)
                        {
                            if (_pendingRestartRequestId is not null &&
                                now - _lastRestartSent >= NetworkConstants.RestartRetryInterval)
                            {
                                _lastRestartSent = now;
                                packet = new RestartPacket { SessionId = _sessionId, RequestId = _pendingRestartRequestId };
                            }
                            else
                            {
                                var axis = LocalAxis(now);
                                var axisChanged = axis != _lastSentAxis;
                                if (axisChanged || timestamp >= _nextInputSendTimestamp)
                                {
                                    // Keep the periodic deadline anchored to its schedule. Reset it
                                    // after an input edge or a long pause so we never send a burst.
                                    _nextInputSendTimestamp = axisChanged || _nextInputSendTimestamp == 0 ||
                                        timestamp - _nextInputSendTimestamp >= NetworkConstants.GuestInputSendIntervalTicks
                                            ? timestamp + NetworkConstants.GuestInputSendIntervalTicks
                                            : _nextInputSendTimestamp + NetworkConstants.GuestInputSendIntervalTicks;
                                    _lastSentAxis = axis;
                                    packet = new InputPacket { SessionId = _sessionId, Sequence = ++_outSequence, Axis = axis };
                                }
                            }
                        }
                    }
                    if (_connection == ConnectionState.Connected && _sessionId is not null &&
                        destination is not null && now - _lastPingSent >= NetworkConstants.PingInterval)
                    {
                        _lastPingSent = now;
                        _pendingPingSequence = ++_pingSequence;
                        _pingSentTimestamp = Stopwatch.GetTimestamp();
                        pingPacket = new PingPacket { SessionId = _sessionId, Sequence = _pendingPingSequence };
                    }
                    if (_pingMs is not null && now - _lastPongSeen > NetworkConstants.PingStaleAfter)
                        _pingMs = null;
                }
                if (socket is not null && destination is not null && packet is not null)
                    await SendQuietlyAsync(socket, destination, packet, cancellationToken, sendBuffer);
                if (socket is not null && destination is not null && pingPacket is not null)
                    await SendQuietlyAsync(socket, destination, pingPacket, cancellationToken, sendBuffer);
            }
        }
        catch (OperationCanceledException) { }
    }

    private StatePacket CreateStatePacket()
    {
        var state = _game.Capture();
        return new StatePacket
        {
            SessionId = _sessionId, Sequence = state.TickNumber,
            LeftY = state.LeftY, RightY = state.RightY,
            BallX = state.BallX, BallY = state.BallY,
            BallVx = state.BallVx, BallVy = state.BallVy,
            LeftScore = state.LeftScore, RightScore = state.RightScore,
            Phase = state.Phase, Countdown = state.Countdown, RoundId = state.RoundId
        };
    }

    // Called while holding _gate. Ping and pong use the same authenticated UDP path as gameplay.
    private void ObservePong(PongPacket packet)
    {
        if (_pendingPingSequence == 0 || packet.Sequence != _pendingPingSequence) return;
        var sample = Stopwatch.GetElapsedTime(_pingSentTimestamp).TotalMilliseconds;
        if (sample >= 0 && sample < NetworkConstants.MaximumPingRoundTrip.TotalMilliseconds)
        {
            _pingMs = _pingMs is { } previous
                ? previous * (1 - NetworkConstants.PingSmoothingAlpha) + sample * NetworkConstants.PingSmoothingAlpha
                : sample;
            _lastPongSeen = DateTime.UtcNow;
        }
        _pendingPingSequence = 0;
    }

    private void ResetPing()
    {
        _pingSequence = _pendingPingSequence = _pingSentTimestamp = 0;
        _pingMs = null;
        _lastPingSent = DateTime.MinValue;
        _lastPongSeen = DateTime.MinValue;
    }

    // Called while holding _gate. A passive tab sends zero, so it cannot override a tab being played.
    private int LocalAxis(DateTime now)
    {
        var newest = DateTime.MinValue;
        var axis = 0;
        foreach (var control in _controllers.Values)
        {
            if (control.Axis == 0 || now - control.Updated > NetworkConstants.InputStaleAfter || control.Updated <= newest)
                continue;
            newest = control.Updated;
            axis = control.Axis;
        }
        return axis;
    }

    private async Task ReceiveAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        // One receive is outstanding at a time, so the datagram and remote address
        // can be handled before the next receive overwrites either buffer.
        var receiveBuffer = new byte[WirePacketCodec.MaxPacketBytes + 1];
        var receiveFrom = NetworkConstants.AnyIpv4Endpoint.Serialize();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var receivedBytes = await socket.Client.ReceiveFromAsync(
                    receiveBuffer.AsMemory(), SocketFlags.None, receiveFrom, cancellationToken);
                if (receivedBytes > WirePacketCodec.MaxPacketBytes || !MayReceiveFrom(socket, receiveFrom)) continue;
                if (!WirePacketCodec.TryDeserialize(receiveBuffer.AsMemory(0, receivedBytes), out var packet))
                    continue;
                if (packet is null) continue;
                await HandlePacketAsync(socket, receiveFrom, packet, cancellationToken);
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

    private async Task HandlePacketAsync(UdpClient socket, SocketAddress remote, WirePacket packet, CancellationToken cancellationToken)
    {
        WirePacket? reply = null;
        IPEndPoint? replyDestination = null;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_socket != socket) return;
            if (_role == PeerRole.Host)
            {
                if (packet is DiscoverPacket)
                {
                    if (_peerEndpoint is null)
                    {
                        reply = new OfferPacket { Port = _udpPort };
                        replyDestination = (IPEndPoint)NetworkConstants.AnyIpv4Endpoint.Create(remote);
                    }
                }
                else if (packet is HelloPacket)
                {
                    if (_peerEndpoint is null)
                    {
                        _peerEndpoint = (IPEndPoint)NetworkConstants.AnyIpv4Endpoint.Create(remote);
                        _peerSocketAddress = _peerEndpoint.Serialize();
                        _sessionId = Guid.NewGuid().ToString("N");
                        _connection = ConnectionState.Connected;
                        _message = "Соперник подключился. Игра началась!";
                        _lastInputSequence = -1;
                        _lastStateSentTick = 0;
                        _lastRestartRequestId = null;
                        ResetPing();
                        _game.StartMatch();
                    }
                    if (_peerSocketAddress?.Equals(remote) == true)
                    {
                        _lastPeerSeen = now;
                        reply = new WelcomePacket { SessionId = _sessionId };
                    }
                }
                else if (_peerSocketAddress?.Equals(remote) == true && _sessionId is not null)
                {
                    switch (packet)
                    {
                        case PingPacket ping when ping.SessionId == _sessionId:
                            _lastPeerSeen = now;
                            reply = new PongPacket { SessionId = _sessionId, Sequence = ping.Sequence };
                            break;
                        case PongPacket pong when pong.SessionId == _sessionId:
                            _lastPeerSeen = now;
                            ObservePong(pong);
                            break;
                        case InputPacket input when input.SessionId == _sessionId &&
                                                    input.Sequence > _lastInputSequence &&
                                                    input.Axis is >= -1 and <= 1:
                            _lastInputSequence = input.Sequence;
                            _remoteAxis = input.Axis;
                            _lastInputSeen = _lastPeerSeen = now;
                            break;
                        case RestartPacket restart when restart.SessionId == _sessionId &&
                                                        !string.IsNullOrEmpty(restart.RequestId):
                            _lastPeerSeen = now;
                            if (restart.RequestId != _lastRestartRequestId)
                            {
                                _lastRestartRequestId = restart.RequestId;
                                _game.StartMatch();
                            }
                            break;
                        case ByePacket bye when bye.SessionId == _sessionId:
                            _peerEndpoint = null;
                            _peerSocketAddress = null;
                            _sessionId = null;
                            _connection = ConnectionState.Waiting;
                            _message = "Соперник вышел. Ожидание нового игрока…";
                            _remoteAxis = 0;
                            _lastStateSentTick = 0;
                            ResetPing();
                            _game.ResetWaiting();
                            break;
                    }
                }
            }
            else if (_role == PeerRole.Guest && _targetSocketAddress?.Equals(remote) == true)
            {
                switch (packet)
                {
                    case WelcomePacket welcome when !string.IsNullOrEmpty(welcome.SessionId):
                        // A delayed welcome from an old match must not replace an active session.
                        if (_connection == ConnectionState.Connected && _sessionId != welcome.SessionId) return;
                        if (_sessionId != welcome.SessionId)
                        {
                            _sessionId = welcome.SessionId;
                            _lastStateSequence = -1;
                            _pendingRestartRequestId = null;
                            _restartAfterRound = 0;
                            ResetPing();
                            _game.ResetWaiting();
                        }
                        _connection = ConnectionState.Connected;
                        _message = "Вы подключились. Игра началась!";
                        _lastPeerSeen = now;
                        break;
                    case PingPacket ping when _sessionId is not null && ping.SessionId == _sessionId:
                        _lastPeerSeen = now;
                        reply = new PongPacket { SessionId = _sessionId, Sequence = ping.Sequence };
                        break;
                    case PongPacket pong when _sessionId is not null && pong.SessionId == _sessionId:
                        _lastPeerSeen = now;
                        ObservePong(pong);
                        break;
                    case StatePacket state when _sessionId is not null && state.SessionId == _sessionId &&
                                                state.Sequence > _lastStateSequence:
                        _lastStateSequence = state.Sequence;
                        _lastPeerSeen = now;
                        _game.Restore(state.ToGameState());
                        if (_pendingRestartRequestId is not null && state.RoundId > _restartAfterRound)
                            _pendingRestartRequestId = null;
                        break;
                    case ByePacket bye when _sessionId is not null && bye.SessionId == _sessionId:
                        _connection = ConnectionState.Connecting;
                        _message = "Соперник вышел. Повторное подключение…";
                        _sessionId = null;
                        _pendingRestartRequestId = null;
                        _restartAfterRound = 0;
                        ResetPing();
                        _game.ResetWaiting();
                        break;
                }
            }
            if (reply is not null && replyDestination is null)
                replyDestination = _role == PeerRole.Host ? _peerEndpoint : _targetEndpoint;
        }
        if (reply is not null && replyDestination is not null)
            await SendQuietlyAsync(socket, replyDestination, reply, cancellationToken);
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

    private void ThrowIfStopping()
    {
        lock (_gate)
            if (_stopping) throw new InvalidOperationException("Приложение завершает работу.");
    }

    private static string[] GetLocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct()
                .Append("127.0.0.1")
                .ToArray();
        }
        catch (NetworkInformationException) { return ["127.0.0.1"]; }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // This runs before Kestrel's StopAsync, while the peer can still send a final UDP packet.
    public Task StoppingAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_shutdownGate)
        {
            if (_shutdownTask is not null) return _shutdownTask;
            lock (_gate) _stopping = true;
            _lifetime.Cancel();
            return _shutdownTask = ShutdownAsync(cancellationToken);
        }
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        try { await _clockTask; }
        finally { await LeaveAsync(cancellationToken); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None); }
        finally
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _transition.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
