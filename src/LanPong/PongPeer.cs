using System.Net;
using System.Net.Sockets;
using LanPong.Bots.Catalog;
using LanPong.Bots.Runtime;

namespace LanPong;

/// <summary>
/// Coordinates browser input and either a remote or local opponent. The partial files share one lock for
/// connection and game state; input, ping, and rejected challenges own their policies.
/// </summary>
internal sealed partial class PongPeer : IHostedLifecycleService, IAsyncDisposable
{
    private readonly ILogger<PongPeer> _logger;
    private readonly Lock _gate = new();
    private readonly string _sourceId = Guid.NewGuid().ToString("N");
    private long _snapshotSequence;
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
    private readonly BotRuntime _bots;
    private PreparedBotSession? _botSession;
    private BotSessionIdentity? _botIdentity;
    private BotDefinition? _identityEffectiveBot;
    private string? _botFallbackMessage;
    private GameEvent[] _confirmedGuestEvents = [];
    private readonly string[] _localAddresses = GetLocalAddresses();
    private readonly Task _clockTask;
    private Task? _shutdownTask;
    private bool _stopping;
    private bool _localOpponentActive => _botSession is not null;
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
    private Guid? _pendingRestartRequestId;
    private int _restartAfterRound;
    private int _confirmedGuestFinishedRound;
    private PaddleSide? _hostSide;
    private Guid? _browserMatchId;
    private string? _browserMatchKey;
    private int _sideRoundId;
    private readonly Func<PaddleSide> _randomSide;
    private PeerRole _role = PeerRole.None;
    private ConnectionState _connection = ConnectionState.Idle;
    private string _message = "Выберите бота или сыграйте с другом по локальной сети.";
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

    public PongPeer(ILogger<PongPeer> logger, BotRuntime bots, Func<PaddleSide>? randomSide = null)
    {
        _logger = logger;
        _bots = bots;
        _randomSide = randomSide ?? (() => Random.Shared.Next(2) == 0 ? PaddleSide.Left : PaddleSide.Right);
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
            var opponentMode = _botSession is not null
                ? OpponentMode.Bot
                : _role != PeerRole.None || _connection == ConnectionState.Searching
                    ? OpponentMode.Lan : OpponentMode.None;
            var opponentFallbackActive = _botIdentity?.FallbackReason is not null;
            var message = opponentFallbackActive
                ? _botFallbackMessage!
                : _message;
            return new PongSnapshot(
                _role, _connection, message, _udpPort, _localAddresses,
                FormatEndpoint(_peerEndpoint ?? _incomingChallengeEndpoint ?? _targetEndpoint),
                state.LeftY, state.RightY, state.BallX, state.BallY,
                state.BallVx, state.BallVy,
                state.LeftScore, state.RightScore, state.Phase,
                state.Countdown, state.TickNumber, state.RoundId, _ping.PingMs, events,
                _localNickname, _peerNickname, opponentMode,
                _botIdentity?.RequestedBotId, _botIdentity?.RequestedName,
                _botIdentity?.EffectiveBotId, _botIdentity?.EffectiveName,
                opponentFallbackActive, _botIdentity?.FallbackReason,
                _hostSide is { } hostSide ? _role == PeerRole.Host ? hostSide : PaddleSides.Opposite(hostSide) : null,
                _browserMatchKey, _sourceId, ++_snapshotSequence, CanRematchLocked());
        }
    }

    public void SetInput(Guid controllerId, Guid matchId, int roundId, int axis)
    {
        lock (_gate)
            if (!_stopping && _connection == ConnectionState.Connected && _hostSide is not null &&
                matchId == _browserMatchId && roundId > 0 && roundId == _game.RoundId)
                _controllers.Set(controllerId, axis, DateTime.UtcNow);
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
            lock (_gate) CancelQuickMatchmakingLocked(clearLobby: true);
            await StartHostingAsync(port, selectedNickname);
        }
        finally { _transition.Release(); }
    }

    internal BotSessionIdentity? BotStatus
    {
        get
        {
            lock (_gate)
                return _botIdentity;
        }
    }

    private void UpdateBotIdentityLocked()
    {
        if (_botSession is not { } bot)
        {
            _botIdentity = null;
            _identityEffectiveBot = null;
            _botFallbackMessage = null;
            return;
        }
        if (ReferenceEquals(_identityEffectiveBot, bot.Effective)) return;
        _identityEffectiveBot = bot.Effective;
        _botIdentity = new BotSessionIdentity(bot.Requested.Id, bot.Requested.Name,
            bot.Effective.Id, bot.Effective.Name, bot.FallbackReason);
        _botFallbackMessage = bot.FallbackReason is null ? null
            : $"Бот «{bot.Requested.Name}» недоступен. " +
              $"Игра продолжается против «{bot.Effective.Name}».";
    }

    public async Task StartBotAsync(string nickname, string? botId, InitialSidePreference side)
    {
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        var selectedNickname = PlayerNickname.Normalize(nickname);
        await _transition.WaitAsync();
        PreparedBotSession? candidate = null;
        try
        {
            lock (_gate)
            {
                if (_stopping) throw new InvalidOperationException("Приложение завершает работу.");
                if (_role != PeerRole.None || _connection != ConnectionState.Idle || _matchmakingTask is not null)
                    throw new InvalidOperationException("Сначала покиньте текущую игру.");
            }

            // File checks/native warmup run on a worker without holding the state lock.
            // A failed selection has not yet changed any peer or controller state.
            candidate = await Task.Run(() => _bots.Prepare(botId));
            lock (_gate)
            {
                if (_stopping) throw new InvalidOperationException("Приложение завершает работу.");
                CancelQuickMatchmakingLocked(clearLobby: true);
            }
            await StopSocketAsync();
            lock (_gate)
            {
                if (_stopping) throw new InvalidOperationException("Приложение завершает работу.");
                var resolvedSide = ResolveInitialSide(side);
                _botSession = candidate;
                UpdateBotIdentityLocked();
                candidate = null; // Peer now owns the fully prepared session.
                _localNickname = selectedNickname;
                _peerNickname = null;
                _role = PeerRole.Host;
                _connection = ConnectionState.Connected;
                _message = "Локальная игра началась!";
                _controllers.Clear();
                _game.StartMatch();
                _hostSide = resolvedSide;
                SetBrowserMatchIdLocked(Guid.NewGuid());
                _sideRoundId = _game.RoundId;
            }
        }
        finally
        {
            try { candidate?.Dispose(); }
            finally { _transition.Release(); }
        }
    }

    // The caller holds _transition. Port zero asks the OS to choose a free UDP port.
    private async Task StartHostingAsync(int port, string nickname, bool socketAlreadyStopped = false,
        bool quickLobby = false)
    {
        ThrowIfStopping();
        if (!socketAlreadyStopped) await StopSocketAsync();
        var socket = CreateUdpSocket(port);
        var actualPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
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
            _localNickname = nickname;
            _quickHostAutoAccept = quickLobby;
            _role = PeerRole.Host;
            _connection = ConnectionState.Waiting;
            _message = "Ожидание второго игрока. Он может найти вашу игру в сети по нику.";
            _udpPort = actualPort;
            _mdns.SetHostPort(actualPort, _localNickname);
            _receiveTask = StartReceiving(socket, stop);
        }
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
            lock (_gate) CancelQuickMatchmakingLocked(clearLobby: true);
            await StartJoiningAsync(ip, port, selectedNickname, cancellationToken);
        }
        finally { _transition.Release(); }
    }

    // The caller holds _transition and supplies a resolved local-network address.
    private async Task StartJoiningAsync(IPAddress ip, int port, string nickname,
        CancellationToken cancellationToken, bool socketAlreadyStopped = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfStopping();
        if (!socketAlreadyStopped) await StopSocketAsync();
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
            _localNickname = nickname;
            _targetEndpoint = new IPEndPoint(targetAddress, port);
            _targetSocketAddress = _targetEndpoint.Serialize();
            _outgoingChallengeId = Guid.NewGuid();
            _challengeStartedAt = DateTime.UtcNow;
            _role = PeerRole.Guest;
            _connection = ConnectionState.Connecting;
            _message = "Подключаемся к игроку…";
            _udpPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            _receiveTask = StartReceiving(socket, stop);
        }
    }

    public void Restart(Guid expectedMatchId, int expectedRoundId)
    {
        PreparedBotSession? failedBot = null;
        List<IDisposable>? retired = null;
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("Приложение завершает работу.");
            ValidateFinishedRoundLocked(expectedMatchId, expectedRoundId);
            if (_role == PeerRole.Host)
            {
                if (_botSession is { } bot)
                {
                    // All candidates were warmed before admission; resets only clear decision state.
                    bot.Reset();
                    UpdateBotIdentityLocked();
                    retired = bot.TakeRetiredControllers();
                    if (!bot.IsPlayable)
                    {
                        failedBot = bot;
                        _botSession = null;
                        UpdateBotIdentityLocked();
                        ResetSocketLocked("Бот не смог продолжить игру. Выберите другого соперника.");
                    }
                }
                if (failedBot is null)
                {
                    StartRematchLocked();
                }
            }
            else if (_role == PeerRole.Guest)
            {
                if (_pendingRestartRequestId is null)
                {
                    _pendingRestartRequestId = Guid.NewGuid();
                    _restartAfterRound = expectedRoundId;
                    _lastRestartSent = DateTime.MinValue;
                }
            }
        }
        BotRuntime.DisposeRetired(retired);
        failedBot?.Dispose();
        if (failedBot is not null)
            throw new InvalidOperationException("Бот не смог продолжить игру. Выберите другого соперника.");
    }


    private void SetBrowserMatchIdLocked(Guid? id)
    {
        _browserMatchId = id;
        _browserMatchKey = id?.ToString("N");
    }

    private PaddleSide ResolveInitialSide(InitialSidePreference preference)
    {
        var side = preference switch
        {
            InitialSidePreference.Left => PaddleSide.Left,
            InitialSidePreference.Right => PaddleSide.Right,
            InitialSidePreference.Random => _randomSide(),
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };
        PaddleSides.Validate(side);
        return side;
    }

    private bool CanRematchLocked() =>
        !_stopping && _connection == ConnectionState.Connected && _hostSide is { } hostSide &&
        PaddleSides.IsPhysical(hostSide) && _browserMatchId is not null && _game.RoundId > 0 &&
        _game.Phase == GamePhase.GameOver &&
        (_role == PeerRole.Host || _role == PeerRole.Guest && _confirmedGuestFinishedRound == _game.RoundId);

    private void ValidateFinishedRoundLocked(Guid expectedMatchId, int expectedRoundId)
    {
        if (_connection != ConnectionState.Connected || _hostSide is null)
            throw new InvalidOperationException("Сначала подключитесь к игре.");
        if (expectedMatchId == Guid.Empty || expectedMatchId != _browserMatchId || expectedRoundId <= 0 ||
            expectedRoundId != _game.RoundId || !CanRematchLocked())
            throw new InvalidOperationException("Реванш доступен только для текущей завершённой игры.");
    }

    // Only authoritative successful rematches change ownership. Bot reset has already succeeded.
    private void StartRematchLocked()
    {
        var nextSide = PaddleSides.Opposite(_hostSide!.Value);
        _controllers.Clear();
        _lastHostAxis = 0;
        _lastInputSentTick = 0;
        _game.StartMatch();
        _hostSide = nextSide;
        _sideRoundId = _game.RoundId;
        _hostTimeline.Reset(nextSide);
        _guestTimeline.Reset(nextSide);
        _lastStateSentTick = _game.TickNumber - NetworkConstants.StateSendIntervalTicks;
    }

    public async Task AcceptChallengeAsync()
    {
        (UdpClient Socket, IPEndPoint Destination, WelcomePacket Welcome) accepted;
        lock (_gate)
            accepted = AcceptChallengeLocked();
        await SendQuietlyAsync(accepted.Socket, accepted.Destination, accepted.Welcome, _lifetime.Token);
    }

    // May also be used by Quick Game as soon as a valid Hello packet arrives.
    private (UdpClient Socket, IPEndPoint Destination, WelcomePacket Welcome) AcceptChallengeLocked()
    {
        if (_stopping || _role != PeerRole.Host || _connection != ConnectionState.IncomingChallenge ||
            _socket is null || _incomingChallengeEndpoint is null ||
            _incomingChallengeSocketAddress is null || _incomingChallengeId is null)
            throw new InvalidOperationException("Нет вызова для принятия.");

        var resolvedSide = ResolveInitialSide(InitialSidePreference.Random);
        var socket = _socket;
        var destination = _incomingChallengeEndpoint;
        _peerEndpoint = destination;
        _peerSocketAddress = _incomingChallengeSocketAddress;
        _acceptedChallengeId = _incomingChallengeId;
        _incomingChallengeEndpoint = null;
        _incomingChallengeSocketAddress = null;
        _incomingChallengeId = null;
        _sessionId = Guid.NewGuid();
        SetBrowserMatchIdLocked(_sessionId);
        _connection = ConnectionState.Connected;
        _message = "Вызов принят. Игра началась!";
        _lastPeerSeen = DateTime.UtcNow;
        _lastStateSentTick = 0;
        _ping.Reset();
        _hostSide = resolvedSide;
        _controllers.Clear();
        _game.StartMatch();
        _sideRoundId = _game.RoundId;
        _hostTimeline.Reset(_hostSide.Value);
        _mdns.SetHostPort(null);
        CancelQuickMatchmakingLocked();
        var welcome = new WelcomePacket
        {
            SessionId = _sessionId.Value, RequestId = _acceptedChallengeId.Value,
            Nickname = _localNickname, HostSide = _hostSide.Value, RoundId = _game.RoundId
        };
        return (socket, destination, welcome);
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
        Task? matchmakingTask = null;
        await _transition.WaitAsync();
        try
        {
            UdpClient? socket;
            IPEndPoint? peer;
            WirePacket? bye;
            lock (_gate)
            {
                matchmakingTask = _matchmakingTask;
                CancelQuickMatchmakingLocked(clearLobby: true);
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
        if (matchmakingTask is not null) await matchmakingTask;
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
