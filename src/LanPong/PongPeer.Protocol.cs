using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace LanPong;

internal sealed partial class PongPeer
{
    private struct PacketActions
    {
        public WirePacket? Reply;
        public IPEndPoint? Destination;
        public UdpClient? SocketToClose;
        public CancellationTokenSource? StopToClose;
    }

    private async Task HandlePacketAsync(
        UdpClient socket, IPEndPoint anyEndpoint, SocketAddress remote, WirePacket packet,
        CancellationToken cancellationToken, ArrayBufferWriter<byte> replyBuffer)
    {
        var actions = new PacketActions();
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            // The socket may have been replaced between the receive filter and decoding.
            if (_socket != socket) return;
            if (_role == PeerRole.Host)
                HandleHostPacketLocked(anyEndpoint, remote, packet, now, ref actions);
            else if (_role == PeerRole.Guest && _targetSocketAddress?.Equals(remote) == true)
                HandleGuestPacketLocked(packet, now, ref actions);

            if (actions.Reply is not null && actions.Destination is null)
                actions.Destination = _role == PeerRole.Host
                    ? _peerEndpoint ?? _incomingChallengeEndpoint
                    : _targetEndpoint;
        }
        CloseDetachedSocket(actions.SocketToClose, actions.StopToClose);
        if (actions.Reply is not null && actions.Destination is not null)
            await SendQuietlyAsync(socket, actions.Destination, actions.Reply, cancellationToken, replyBuffer);
    }

    // All packet handlers below run under _gate. Network sends happen after the lock is released.
    private void HandleHostPacketLocked(
        IPEndPoint anyEndpoint, SocketAddress remote, WirePacket packet, DateTime now, ref PacketActions actions)
    {
        switch (packet)
        {
            case HelloPacket hello:
                HandleHostHelloLocked(anyEndpoint, remote, hello, now, ref actions);
                break;
            case CancelChallengePacket cancel:
                HandleHostCancelLocked(remote, cancel, now, ref actions);
                break;
            default:
                if (_peerSocketAddress?.Equals(remote) == true && _sessionId is not null)
                    HandleHostSessionPacketLocked(packet, now, ref actions);
                break;
        }
    }

    private void HandleHostHelloLocked(
        IPEndPoint anyEndpoint, SocketAddress remote, HelloPacket hello, DateTime now, ref PacketActions actions)
    {
        if (_peerEndpoint is not null)
        {
            if (_peerSocketAddress?.Equals(remote) == true && hello.RequestId == _acceptedChallengeId)
            {
                _lastPeerSeen = now;
                actions.Reply = new WelcomePacket
                {
                    SessionId = _sessionId!.Value, RequestId = _acceptedChallengeId!.Value,
                    Nickname = _localNickname, HostSide = _hostSide!.Value, RoundId = _game.RoundId
                };
            }
        }
        else if (_rejectedChallenges.Contains(remote, hello.RequestId, now))
        {
            actions.Reply = new ChallengeDeclinedPacket { RequestId = hello.RequestId };
            actions.Destination = (IPEndPoint)anyEndpoint.Create(remote);
        }
        else if (_incomingChallengeEndpoint is null)
        {
            _incomingChallengeEndpoint = (IPEndPoint)anyEndpoint.Create(remote);
            _incomingChallengeSocketAddress = _incomingChallengeEndpoint.Serialize();
            _incomingChallengeId = hello.RequestId;
            _peerNickname = hello.Nickname;
            _challengeStartedAt = now;
            _lastChallengeSeen = now;
            _connection = ConnectionState.IncomingChallenge;
            _message = "Входящий вызов. Примите или отклоните его.";
            _mdns.SetHostPort(null);
            if (_quickHostAutoAccept)
            {
                var accepted = AcceptChallengeLocked();
                actions.Reply = accepted.Welcome;
                actions.Destination = accepted.Destination;
            }
            else
                actions.Reply = new ChallengePendingPacket
                {
                    RequestId = hello.RequestId, Nickname = _localNickname
                };
        }
        else if (_incomingChallengeSocketAddress?.Equals(remote) == true &&
                 hello.RequestId == _incomingChallengeId)
        {
            _lastChallengeSeen = now;
            actions.Reply = new ChallengePendingPacket
            {
                RequestId = hello.RequestId, Nickname = _localNickname
            };
        }
        else
        {
            actions.Reply = new ChallengeDeclinedPacket { RequestId = hello.RequestId };
            actions.Destination = (IPEndPoint)anyEndpoint.Create(remote);
            _rejectedChallenges.Remember(remote, hello.RequestId, now);
        }
    }

    private void HandleHostCancelLocked(SocketAddress remote, CancelChallengePacket cancel, DateTime now,
        ref PacketActions actions)
    {
        _rejectedChallenges.Remember(remote, cancel.RequestId, now);
        if (_connection == ConnectionState.IncomingChallenge &&
            _incomingChallengeSocketAddress?.Equals(remote) == true &&
            cancel.RequestId == _incomingChallengeId)
            ClearIncomingChallengeLocked("Соперник отменил вызов. Ожидание другого игрока…");
        else if (_connection == ConnectionState.Connected &&
                 _peerSocketAddress?.Equals(remote) == true &&
                 cancel.RequestId == _acceptedChallengeId)
            (actions.SocketToClose, actions.StopToClose, _) = EndHostMatchLocked(
                "Соперник отменил вызов. Ожидание другого игрока…",
                "Соперник отменил игру. Нажмите «Быстрая игра», чтобы сыграть снова.");
    }

    private void HandleHostSessionPacketLocked(WirePacket packet, DateTime now, ref PacketActions actions)
    {
        switch (packet)
        {
            case PingPacket ping when ping.SessionId == _sessionId:
                _lastPeerSeen = now;
                actions.Reply = new PongPacket { SessionId = _sessionId!.Value, Sequence = ping.Sequence };
                break;
            case PongPacket pong when pong.SessionId == _sessionId:
                _lastPeerSeen = now;
                _ping.Observe(pong);
                break;
            case InputPacket input when input.SessionId == _sessionId && input.RoundId == _game.RoundId:
                _lastPeerSeen = now;
                if (_hostTimeline.Receive(input))
                    _lastStateSentTick = Math.Min(_lastStateSentTick,
                        _game.TickNumber - NetworkConstants.StateSendIntervalTicks);
                break;
            case RestartPacket restart when restart.SessionId == _sessionId:
                _lastPeerSeen = now;
                if (_connection == ConnectionState.Connected && restart.ExpectedRoundId > 0 &&
                    restart.ExpectedRoundId == _game.RoundId && _game.Phase == GamePhase.GameOver)
                    StartRematchLocked();
                break;
            case ByePacket bye when bye.SessionId == _sessionId:
                (actions.SocketToClose, actions.StopToClose, _) = EndHostMatchLocked(
                    "Соперник вышел. Ожидание нового игрока…",
                    "Соперник вышел. Нажмите «Быстрая игра», чтобы сыграть снова.");
                break;
        }
    }

    private bool AcceptsSideStateLocked(StatePacket state)
    {
        if (_hostSide is not { } hostSide || state.RoundId < _sideRoundId) return false;
        var expected = (state.RoundId - _sideRoundId) % 2 == 0 ? hostSide : PaddleSides.Opposite(hostSide);
        return state.HostSide == expected;
    }

    private void HandleGuestPacketLocked(WirePacket packet, DateTime now, ref PacketActions actions)
    {
        switch (packet)
        {
            case ChallengePendingPacket pending when pending.RequestId == _outgoingChallengeId &&
                                                     _connection is (ConnectionState.Connecting or ConnectionState.AwaitingAcceptance):
                _connection = ConnectionState.AwaitingAcceptance;
                _peerNickname = pending.Nickname;
                _message = "Вызов отправлен. Ждём решения соперника…";
                _lastPeerSeen = now;
                break;
            case ChallengeDeclinedPacket declined when declined.RequestId == _outgoingChallengeId &&
                                                       _connection is (ConnectionState.Connecting or ConnectionState.AwaitingAcceptance):
                (actions.SocketToClose, actions.StopToClose, _) = ResetSocketLocked(
                    "Вызов отклонён. Выберите другую игру или попробуйте позже.");
                break;
            case WelcomePacket welcome when welcome.RequestId == _outgoingChallengeId &&
                                            _connection is (ConnectionState.Connecting or ConnectionState.AwaitingAcceptance):
                _sessionId = welcome.SessionId;
                SetBrowserMatchIdLocked(welcome.SessionId);
                _lastStateSequence = -1;
                _pendingRestartRequestId = null;
                _restartAfterRound = 0;
                _confirmedGuestFinishedRound = 0;
                _ping.Reset();
                _game.ResetWaiting();
                _hostSide = welcome.HostSide;
                _sideRoundId = welcome.RoundId;
                _game.RestoreCheckpoint(_game.Capture() with { RoundId = welcome.RoundId });
                _controllers.Clear();
                _guestTimeline.Reset(welcome.HostSide);
                _lastInputSentTick = 0;
                _connection = ConnectionState.Connected;
                _peerNickname = welcome.Nickname;
                _message = "Вы подключились. Игра началась!";
                CancelQuickMatchmakingLocked();
                _outgoingChallengeId = null;
                _lastPeerSeen = now;
                break;
            case PingPacket ping when _sessionId is not null && ping.SessionId == _sessionId:
                _lastPeerSeen = now;
                actions.Reply = new PongPacket { SessionId = _sessionId.Value, Sequence = ping.Sequence };
                break;
            case PongPacket pong when _sessionId is not null && pong.SessionId == _sessionId:
                _lastPeerSeen = now;
                _ping.Observe(pong);
                break;
            case StatePacket state when _sessionId is not null && state.SessionId == _sessionId &&
                                        state.Sequence > _lastStateSequence && AcceptsSideStateLocked(state):
                if (state.RoundId != _sideRoundId)
                {
                    _hostSide = state.HostSide;
                    _sideRoundId = state.RoundId;
                    _controllers.Clear();
                    _guestTimeline.Reset(state.HostSide);
                    _lastInputSentTick = 0;
                }
                _lastStateSequence = state.Sequence;
                _lastPeerSeen = now;
                _confirmedGuestEvents = state.RecentEvents!;
                // Prediction may score the winning goal before the host. Admission and cancellation
                // must follow this accepted authoritative phase, before speculative replay changes it.
                _confirmedGuestFinishedRound = state.Phase == GamePhase.GameOver ? state.RoundId : 0;
                _guestTimeline.Reconcile(state.ToGameState(), state.HostAxis, _ping.PingMs,
                    _controllers.GetAxis(now));
                if (_game.TickNumber < _lastInputSentTick)
                    _lastInputSentTick = _game.TickNumber - 1;
                if (_game.TickNumber > _lastInputSentTick && _guestTimeline.HasCurrentInput)
                {
                    _lastInputSentTick = _game.TickNumber;
                    actions.Reply = _guestTimeline.CreateInputPacket(_sessionId.Value, ++_outSequence);
                }
                if (_pendingRestartRequestId is not null && (state.RoundId > _restartAfterRound ||
                    state.RoundId == _restartAfterRound && state.Phase != GamePhase.GameOver))
                {
                    _pendingRestartRequestId = null;
                    _restartAfterRound = 0;
                    _lastRestartSent = DateTime.MinValue;
                }
                break;
            // Welcome and Bye can arrive out of order while the first handshake is in flight.
            case ByePacket bye when bye.SessionId == _sessionId ||
                                      _connection is (ConnectionState.Connecting or ConnectionState.AwaitingAcceptance) &&
                                      _sessionId is null:
                (actions.SocketToClose, actions.StopToClose, _) = ResetSocketLocked(
                    "Соперник вышел. Подключитесь к новой игре вручную.");
                break;
        }
    }
}
