using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanPong;

/// <summary>One local player and one remote player, connected directly over UDP.</summary>
internal sealed class PongPeer : IAsyncDisposable
{
    private readonly ILogger<PongPeer> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GameEngine _game = new();
    private readonly Dictionary<Guid, (int Axis, DateTime Updated)> _controllers = [];
    private readonly string[] _localAddresses = GetLocalAddresses();
    private readonly Task _clockTask;

    private UdpClient? _socket;
    private CancellationTokenSource? _socketStop;
    private Task? _receiveTask;
    private IPEndPoint? _peerEndpoint;
    private IPEndPoint? _targetEndpoint;
    private string? _sessionId;
    private string? _lastRestartRequestId;
    private string? _pendingRestartRequestId;
    private int _restartAfterRound;
    private string _role = "none";
    private string _connection = "idle";
    private string _message = "Создайте игру или подключитесь к другу.";
    private int _udpPort;
    private int _remoteAxis;
    private long _outSequence;
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
    private DateTime _lastInputSent = DateTime.MinValue;
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
                state.LeftScore, state.RightScore, GamePhaseWire.Format(state.Phase),
                state.Countdown, state.TickNumber, state.RoundId, _pingMs);
        }
    }

    public void SetInput(Guid controllerId, int axis)
    {
        lock (_gate) _controllers[controllerId] = (Math.Clamp(axis, -1, 1), DateTime.UtcNow);
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
            await StopSocketAsync();
            var socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            var stop = new CancellationTokenSource();
            lock (_gate)
            {
                _socket = socket;
                _socketStop = stop;
                _role = "host";
                _connection = "waiting";
                _message = "Ожидание второго игрока. Передайте ему ваш IP-адрес.";
                _udpPort = port;
            }
            _receiveTask = Task.Run(() => ReceiveAsync(socket, stop.Token));
        }
        finally { _transition.Release(); }
    }

    public async Task JoinAsync(string address, int port)
    {
        ValidatePort(port);
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Введите IP-адрес создателя игры.");
        var addresses = await Dns.GetHostAddressesAsync(address.Trim());
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new ArgumentException("Нужен IPv4-адрес компьютера в локальной сети.");

        await _transition.WaitAsync();
        try
        {
            await StopSocketAsync();
            var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var stop = new CancellationTokenSource();
            lock (_gate)
            {
                _socket = socket;
                _socketStop = stop;
                _targetEndpoint = new IPEndPoint(ip, port);
                _role = "guest";
                _connection = "connecting";
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
            if (_connection != "connected") throw new InvalidOperationException("Сначала подключитесь к игре.");
            if (_role == "host") _game.StartMatch();
            else if (_role == "guest")
            {
                _pendingRestartRequestId = Guid.NewGuid().ToString("N");
                _restartAfterRound = _game.RoundId;
                _lastRestartSent = DateTime.MinValue;
            }
        }
    }

    public async Task LeaveAsync()
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
                await SendQuietlyAsync(socket, peer, bye, CancellationToken.None);
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
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1300));
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
                try { await Task.Delay(50, timeout.Token); }
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
            _sessionId = _lastRestartRequestId = _pendingRestartRequestId = null;
            _restartAfterRound = 0;
            _role = "none";
            _connection = "idle";
            _message = "Создайте игру или подключитесь к другу.";
            _udpPort = _remoteAxis = 0;
            _controllers.Clear();
            _outSequence = 0;
            _lastInputSequence = _lastStateSequence = -1;
            _lastStateSentTick = 0;
            _lastPeerSeen = _lastInputSeen = _lastHelloSent = _lastInputSent = _lastRestartSent = DateTime.MinValue;
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
        const double fixedStep = 1.0 / 60;
        const int maxCatchUpSteps = 4;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(fixedStep));
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

                    if (_role == "host")
                    {
                        if (_peerEndpoint is not null && now - _lastPeerSeen > TimeSpan.FromSeconds(5))
                        {
                            _peerEndpoint = null;
                            _sessionId = null;
                            _connection = "waiting";
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
                            var remoteAxis = now - _lastInputSeen < TimeSpan.FromMilliseconds(350) ? _remoteAxis : 0;
                            var localAxis = LocalAxis(now);
                            // PeriodicTimer coalesces missed wakes. Measure elapsed monotonic time
                            // and catch up a bounded number of fixed physics steps instead.
                            accumulatedTime = Math.Min(accumulatedTime + elapsed, fixedStep * maxCatchUpSteps);
                            for (var step = 0; step < maxCatchUpSteps && accumulatedTime >= fixedStep; step++)
                            {
                                _game.Advance(fixedStep, localAxis, remoteAxis);
                                accumulatedTime -= fixedStep;
                            }
                            if (_game.TickNumber - _lastStateSentTick >= 2)
                            {
                                packet = CreateStatePacket();
                                _lastStateSentTick = _game.TickNumber;
                            }
                        }
                        else accumulatedTime = 0;
                    }
                    else if (_role == "guest" && _targetEndpoint is not null)
                    {
                        accumulatedTime = 0;
                        destination = _targetEndpoint;
                        if (_connection == "connected" && now - _lastPeerSeen > TimeSpan.FromSeconds(5))
                        {
                            _connection = "connecting";
                            _message = "Связь потеряна. Повторное подключение…";
                            _sessionId = null;
                            _pendingRestartRequestId = null;
                            _restartAfterRound = 0;
                            ResetPing();
                            _game.ResetWaiting();
                        }
                        if (_connection == "connecting" && now - _lastHelloSent >= TimeSpan.FromMilliseconds(500))
                        {
                            _lastHelloSent = now;
                            packet = new HelloPacket();
                        }
                        else if (_connection == "connected" && _sessionId is not null)
                        {
                            if (_pendingRestartRequestId is not null &&
                                now - _lastRestartSent >= TimeSpan.FromMilliseconds(250))
                            {
                                _lastRestartSent = now;
                                packet = new RestartPacket { SessionId = _sessionId, RequestId = _pendingRestartRequestId };
                            }
                            else if (now - _lastInputSent >= TimeSpan.FromMilliseconds(33))
                            {
                                _lastInputSent = now;
                                packet = new InputPacket { SessionId = _sessionId, Sequence = ++_outSequence, Axis = LocalAxis(now) };
                            }
                        }
                    }
                    if (_connection == "connected" && _sessionId is not null &&
                        destination is not null && now - _lastPingSent >= TimeSpan.FromSeconds(1))
                    {
                        _lastPingSent = now;
                        _pendingPingSequence = ++_pingSequence;
                        _pingSentTimestamp = Stopwatch.GetTimestamp();
                        pingPacket = new PingPacket { SessionId = _sessionId, Sequence = _pendingPingSequence };
                    }
                    if (_pingMs is not null && now - _lastPongSeen > TimeSpan.FromSeconds(4))
                        _pingMs = null;
                }
                if (socket is not null && destination is not null && packet is not null)
                    await SendQuietlyAsync(socket, destination, packet, cancellationToken);
                if (socket is not null && destination is not null && pingPacket is not null)
                    await SendQuietlyAsync(socket, destination, pingPacket, cancellationToken);
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
            Phase = GamePhaseWire.Format(state.Phase), Countdown = state.Countdown, RoundId = state.RoundId
        };
    }

    // Called while holding _gate. Ping and pong use the same authenticated UDP path as gameplay.
    private void ObservePong(PongPacket packet)
    {
        if (_pendingPingSequence == 0 || packet.Sequence != _pendingPingSequence) return;
        var sample = Stopwatch.GetElapsedTime(_pingSentTimestamp).TotalMilliseconds;
        if (sample is >= 0 and < 5000)
        {
            _pingMs = _pingMs is { } previous ? previous * 0.75 + sample * 0.25 : sample;
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
            if (control.Axis == 0 || now - control.Updated > TimeSpan.FromMilliseconds(350) || control.Updated <= newest)
                continue;
            newest = control.Updated;
            axis = control.Axis;
        }
        return axis;
    }

    private async Task ReceiveAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveAsync(cancellationToken);
                if (!WirePacketCodec.TryDeserialize(received.Buffer, out var packet)) continue;
                if (packet is null) continue;
                await HandlePacketAsync(socket, received.RemoteEndPoint, packet, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "UDP receive failed");
                try { await Task.Delay(100, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandlePacketAsync(UdpClient socket, IPEndPoint remote, WirePacket packet, CancellationToken cancellationToken)
    {
        WirePacket? reply = null;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_socket != socket) return;
            if (_role == "host")
            {
                if (packet is DiscoverPacket)
                {
                    if (_peerEndpoint is null) reply = new OfferPacket { Port = _udpPort };
                }
                else if (packet is HelloPacket)
                {
                    if (_peerEndpoint is null)
                    {
                        _peerEndpoint = remote;
                        _sessionId = Guid.NewGuid().ToString("N");
                        _connection = "connected";
                        _message = "Соперник подключился. Игра началась!";
                        _lastInputSequence = -1;
                        _lastStateSentTick = 0;
                        _lastRestartRequestId = null;
                        ResetPing();
                        _game.StartMatch();
                    }
                    if (_peerEndpoint.Equals(remote))
                    {
                        _lastPeerSeen = now;
                        reply = new WelcomePacket { SessionId = _sessionId };
                    }
                }
                else if (_peerEndpoint?.Equals(remote) == true && _sessionId is not null)
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
                            _sessionId = null;
                            _connection = "waiting";
                            _message = "Соперник вышел. Ожидание нового игрока…";
                            _remoteAxis = 0;
                            _lastStateSentTick = 0;
                            ResetPing();
                            _game.ResetWaiting();
                            break;
                    }
                }
            }
            else if (_role == "guest" && _targetEndpoint?.Equals(remote) == true)
            {
                switch (packet)
                {
                    case WelcomePacket welcome when !string.IsNullOrEmpty(welcome.SessionId):
                        // A delayed welcome from an old match must not replace an active session.
                        if (_connection == "connected" && _sessionId != welcome.SessionId) return;
                        if (_sessionId != welcome.SessionId)
                        {
                            _sessionId = welcome.SessionId;
                            _lastStateSequence = -1;
                            _pendingRestartRequestId = null;
                            _restartAfterRound = 0;
                            ResetPing();
                            _game.ResetWaiting();
                        }
                        _connection = "connected";
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
                        _connection = "connecting";
                        _message = "Соперник вышел. Повторное подключение…";
                        _sessionId = null;
                        _pendingRestartRequestId = null;
                        _restartAfterRound = 0;
                        ResetPing();
                        _game.ResetWaiting();
                        break;
                }
            }
        }
        if (reply is not null) await SendQuietlyAsync(socket, remote, reply, cancellationToken);
    }

    private async Task SendQuietlyAsync(UdpClient socket, IPEndPoint destination, WirePacket packet, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = WirePacketCodec.Serialize(packet);
            await socket.SendAsync(bytes, destination, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex) { _logger.LogDebug(ex, "UDP send failed to {Destination}", destination); }
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Порт должен быть от 1 до 65535.");
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

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _transition.WaitAsync();
        try { await StopSocketAsync(); }
        finally { _transition.Release(); }
        try { await _clockTask; }
        catch (OperationCanceledException) { }
        _transition.Dispose();
        _lifetime.Dispose();
    }
}
