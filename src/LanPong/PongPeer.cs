using System.Net;
using System.Net.Sockets;

namespace LanPong;

/// <summary>
/// Coordinates one local and one remote player. The partial files share one lock for
/// connection and game state; input, ping, and rejected challenges own their policies.
/// </summary>
internal sealed partial class PongPeer : IHostedLifecycleService, IAsyncDisposable
{
    private readonly ILogger<PongPeer> _logger;
    private readonly Lock _gate = new();
    private readonly Lock _shutdownGate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GameEngine _game = new();
    private readonly HostRollbackTimeline _hostTimeline;
    private readonly GuestPredictionTimeline _guestTimeline;
    private readonly MdnsDiscovery _mdns;
    private readonly LocalControllerInputs _controllers = new();
    private readonly RejectedChallengeCache _rejectedChallenges = new();
    private readonly PeerPingTracker _ping = new();
    private GameEvent[] _confirmedGuestEvents = [];
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
    private IPEndPoint? _incomingChallengeEndpoint;
    private SocketAddress? _peerSocketAddress;
    private SocketAddress? _targetSocketAddress;
    private SocketAddress? _incomingChallengeSocketAddress;
    private Guid? _sessionId;
    private Guid? _outgoingChallengeId;
    private Guid? _incomingChallengeId;
    private Guid? _acceptedChallengeId;
    private Guid? _lastRestartRequestId;
    private Guid? _pendingRestartRequestId;
    private int _restartAfterRound;
    private PeerRole _role = PeerRole.None;
    private ConnectionState _connection = ConnectionState.Idle;
    private string _message = "Создайте игру или подключитесь к другу.";
    private string _localNickname = $"Игрок {Random.Shared.Next(1000, 10000)}";
    private string? _peerNickname;
    private int _udpPort;
    private long _outSequence;
    private long _lastInputSentTick;
    private int _lastHostAxis;
    private long _lastStateSequence = -1;
    private long _lastStateSentTick;
    private DateTime _lastPeerSeen = DateTime.MinValue;
    private DateTime _challengeStartedAt = DateTime.MinValue;
    private DateTime _lastChallengeSeen = DateTime.MinValue;
    private DateTime _lastHelloSent = DateTime.MinValue;
    private DateTime _lastRestartSent = DateTime.MinValue;

    public PongPeer(ILogger<PongPeer> logger)
    {
        _logger = logger;
        _mdns = new MdnsDiscovery(logger);
        _hostTimeline = new HostRollbackTimeline(_game);
        _guestTimeline = new GuestPredictionTimeline(_game);
        _clockTask = Task.Run(() => ClockAsync(_lifetime.Token));
    }

    public PongSnapshot Snapshot()
    {
        lock (_gate)
        {
            var state = _game.Capture();
            var events = _role == PeerRole.Guest
                ? _connection == ConnectionState.Connected && _guestTimeline.Started
                    ? _confirmedGuestEvents : []
                : state.RecentEvents.ToArray();
            return new PongSnapshot(
                _role, _connection, _message, _udpPort, _localAddresses,
                FormatEndpoint(_peerEndpoint ?? _incomingChallengeEndpoint ?? _targetEndpoint),
                state.LeftY, state.RightY, state.BallX, state.BallY,
                state.BallVx, state.BallVy,
                state.LeftScore, state.RightScore, state.Phase,
                state.Countdown, state.TickNumber, state.RoundId, _ping.PingMs, events,
                _localNickname, _peerNickname);
        }
    }

    public void SetInput(Guid controllerId, int axis)
    {
        lock (_gate)
            if (!_stopping) _controllers.Set(controllerId, axis, DateTime.UtcNow);
    }

    public void RemoveController(Guid controllerId)
    {
        lock (_gate) _controllers.Remove(controllerId);
    }

    public async Task HostAsync(int port, string nickname)
    {
        ValidatePort(port);
        var selectedNickname = PlayerNickname.Normalize(nickname);
        await _transition.WaitAsync();
        try
        {
            ThrowIfStopping();
            await StopSocketAsync();
            var socket = CreateUdpSocket(port);
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
                _localNickname = selectedNickname;
                _role = PeerRole.Host;
                _connection = ConnectionState.Waiting;
                _message = "Ожидание второго игрока. Он может найти вашу игру в сети по нику.";
                _udpPort = port;
                _mdns.SetHostPort(port, _localNickname);
                _receiveTask = Task.Run(() => ReceiveAsync(socket, stop));
            }
        }
        finally { _transition.Release(); }
    }

    public async Task JoinAsync(string address, int port, string nickname,
        CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        var selectedNickname = PlayerNickname.Normalize(nickname);
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Введите IP-адрес создателя игры.");
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfStopping();
        var input = address.Trim();
        if (input.Length > 2 && input[0] == '[' && input[^1] == ']')
            input = input[1..^1];
        IPAddress? ip;
        if (!IPAddress.TryParse(input, out ip))
        {
            var addresses = await Dns.GetHostAddressesAsync(input, cancellationToken);
            // Preserve the previous IPv4 behavior for names with both A and AAAA records.
            ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        }
        if (ip is null) throw new ArgumentException("Нужен IPv4- или IPv6-адрес компьютера в локальной сети.");

        await _transition.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfStopping();
            await StopSocketAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var socket = CreateUdpSocket(0);
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 &&
                socket.Client.AddressFamily != AddressFamily.InterNetworkV6)
            {
                socket.Dispose();
                throw new ArgumentException("IPv6 недоступен на этом компьютере.");
            }
            // A dual-mode socket receives IPv4 peers as IPv4-mapped IPv6 addresses.
            var targetAddress = socket.Client.AddressFamily == AddressFamily.InterNetworkV6 &&
                                ip.AddressFamily == AddressFamily.InterNetwork
                ? ip.MapToIPv6()
                : ip;
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
                _localNickname = selectedNickname;
                _targetEndpoint = new IPEndPoint(targetAddress, port);
                _targetSocketAddress = _targetEndpoint.Serialize();
                _outgoingChallengeId = Guid.NewGuid();
                _challengeStartedAt = DateTime.UtcNow;
                _role = PeerRole.Guest;
                _connection = ConnectionState.Connecting;
                _message = "Подключаемся к игроку…";
                _udpPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
                _receiveTask = Task.Run(() => ReceiveAsync(socket, stop));
            }
        }
        finally { _transition.Release(); }
    }

    public void Restart()
    {
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("Приложение завершает работу.");
            if (_connection != ConnectionState.Connected) throw new InvalidOperationException("Сначала подключитесь к игре.");
            if (_role == PeerRole.Host)
            {
                _game.StartMatch();
                _hostTimeline.Reset();
            }
            else if (_role == PeerRole.Guest)
            {
                _pendingRestartRequestId = Guid.NewGuid();
                _restartAfterRound = _game.RoundId;
                _lastRestartSent = DateTime.MinValue;
            }
        }
    }

    public async Task AcceptChallengeAsync()
    {
        UdpClient socket;
        IPEndPoint destination;
        WelcomePacket welcome;
        lock (_gate)
        {
            if (_stopping || _role != PeerRole.Host || _connection != ConnectionState.IncomingChallenge ||
                _socket is null || _incomingChallengeEndpoint is null ||
                _incomingChallengeSocketAddress is null || _incomingChallengeId is null)
                throw new InvalidOperationException("Нет вызова для принятия.");

            socket = _socket;
            destination = _incomingChallengeEndpoint;
            _peerEndpoint = destination;
            _peerSocketAddress = _incomingChallengeSocketAddress;
            _acceptedChallengeId = _incomingChallengeId;
            _incomingChallengeEndpoint = null;
            _incomingChallengeSocketAddress = null;
            _incomingChallengeId = null;
            _sessionId = Guid.NewGuid();
            _connection = ConnectionState.Connected;
            _message = "Вызов принят. Игра началась!";
            _lastPeerSeen = DateTime.UtcNow;
            _lastStateSentTick = 0;
            _lastRestartRequestId = null;
            _ping.Reset();
            _game.StartMatch();
            _hostTimeline.Reset();
            _mdns.SetHostPort(null);
            welcome = new WelcomePacket
            {
                SessionId = _sessionId.Value, RequestId = _acceptedChallengeId.Value,
                Nickname = _localNickname
            };
        }
        await SendQuietlyAsync(socket, destination, welcome, _lifetime.Token);
    }

    public async Task DeclineChallengeAsync()
    {
        UdpClient socket;
        IPEndPoint destination;
        ChallengeDeclinedPacket declined;
        lock (_gate)
        {
            if (_stopping || _role != PeerRole.Host || _connection != ConnectionState.IncomingChallenge ||
                _socket is null || _incomingChallengeEndpoint is null || _incomingChallengeId is null)
                throw new InvalidOperationException("Нет вызова для отклонения.");

            socket = _socket;
            destination = _incomingChallengeEndpoint;
            declined = new ChallengeDeclinedPacket { RequestId = _incomingChallengeId.Value };
            ClearIncomingChallengeLocked("Вызов отклонён. Ожидание другого игрока…");
        }
        await SendQuietlyAsync(socket, destination, declined, _lifetime.Token);
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
                peer = _peerEndpoint ?? _incomingChallengeEndpoint ?? _targetEndpoint;
                bye = _sessionId is not null
                    ? new ByePacket { SessionId = _sessionId.Value }
                    : _role == PeerRole.Guest && _outgoingChallengeId is not null
                        ? new CancelChallengePacket { RequestId = _outgoingChallengeId.Value }
                        : _role == PeerRole.Host && _incomingChallengeId is not null
                            ? new ChallengeDeclinedPacket { RequestId = _incomingChallengeId.Value }
                            : null;
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

    public Task<IReadOnlyList<DiscoveredHost>> DiscoverAsync(CancellationToken cancellationToken) =>
        _mdns.DiscoverAsync(cancellationToken);

    private void ThrowIfStopping()
    {
        lock (_gate)
            if (_stopping) throw new InvalidOperationException("Приложение завершает работу.");
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
                await _mdns.DisposeAsync();
                _transition.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
