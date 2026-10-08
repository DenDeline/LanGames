using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LanPong;

internal sealed partial class PongPeer
{
    private struct ClockActions
    {
        public UdpClient? Socket;
        public IPEndPoint? Destination;
        public WirePacket? Packet;
        public InputPacket? Input;
        public WirePacket? Ping;
        public UdpClient? SocketToClose;
        public CancellationTokenSource? StopToClose;
        public List<IDisposable>? BotControllersToDispose;
        public PreparedBotSession? BotToDispose;
    }

    private async Task ClockAsync(CancellationToken cancellationToken)
    {
        const double fixedStep = GameConstants.FixedStepSeconds;
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
                ClockActions actions;
                var now = DateTime.UtcNow;
                lock (_gate)
                {
                    actions = new ClockActions { Socket = _socket };
                    if (_localOpponentActive)
                        TickLocalOpponentLocked(now, elapsed, ref accumulatedTime, ref actions);
                    else if (actions.Socket is null)
                    {
                        accumulatedTime = 0;
                        continue;
                    }
                    else if (_role == PeerRole.Host)
                        TickHostLocked(now, elapsed, ref accumulatedTime, ref actions);
                    else if (_role == PeerRole.Guest && _targetEndpoint is not null)
                        TickGuestLocked(now, elapsed, ref accumulatedTime, ref actions);
                    if (_connection == ConnectionState.Connected && _sessionId is not null &&
                        actions.Destination is not null)
                        actions.Ping = _ping.CreatePing(_sessionId.Value, now);
                    _ping.Expire(now);
                }
                BotRuntime.DisposeRetired(actions.BotControllersToDispose);
                actions.BotToDispose?.Dispose();
                if (actions.Socket is { } socket && actions.Destination is { } destination)
                {
                    if (actions.Packet is not null)
                        await SendQuietlyAsync(socket, destination, actions.Packet, cancellationToken, sendBuffer);
                    if (actions.Input is not null)
                        await SendQuietlyAsync(socket, destination, actions.Input, cancellationToken, sendBuffer);
                    if (actions.Ping is not null)
                        await SendQuietlyAsync(socket, destination, actions.Ping, cancellationToken, sendBuffer);
                }
                CloseDetachedSocket(actions.SocketToClose, actions.StopToClose);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Called under _gate. The local match uses the same fixed-step engine, without UDP or rollback.
    private void TickLocalOpponentLocked(DateTime now, double elapsed, ref double accumulatedTime,
        ref ClockActions actions)
    {
        var bot = _botSession!;
        var leftAxis = _controllers.GetAxis(now);
        accumulatedTime = Math.Min(accumulatedTime + elapsed,
            GameConstants.FixedStepSeconds * NetworkConstants.MaximumSimulationCatchUpSteps);
        for (var step = 0; step < NetworkConstants.MaximumSimulationCatchUpSteps &&
                           accumulatedTime >= GameConstants.FixedStepSeconds; step++)
        {
            var rightAxis = Math.Clamp(bot.GetAxis(_game.Capture()), -1, 1);
            UpdateBotIdentityLocked();
            if (!bot.IsPlayable)
            {
                _botSession = null;
                UpdateBotIdentityLocked();
                ResetSocketLocked("Бот не смог продолжить игру. Выберите другого соперника.");
                actions.BotToDispose = bot;
                actions.BotControllersToDispose = bot.TakeRetiredControllers();
                accumulatedTime = 0;
                return;
            }
            _game.Advance(GameConstants.FixedStepSeconds, leftAxis, rightAxis);
            accumulatedTime -= GameConstants.FixedStepSeconds;
        }
        actions.BotControllersToDispose = bot.TakeRetiredControllers();
    }

    // Called under _gate; the clock owns accumulatedTime and its send buffer.
    private void TickHostLocked(DateTime now, double elapsed, ref double accumulatedTime, ref ClockActions actions)
    {
        if (_connection == ConnectionState.IncomingChallenge &&
            (now - _challengeStartedAt >= NetworkConstants.ChallengeLifetime ||
             now - _lastChallengeSeen >= NetworkConstants.PeerIdleTimeout) &&
            _incomingChallengeEndpoint is not null && _incomingChallengeId is not null)
        {
            actions.Destination = _incomingChallengeEndpoint;
            actions.Packet = new ChallengeDeclinedPacket { RequestId = _incomingChallengeId.Value };
            ClearIncomingChallengeLocked("Вызов истёк. Ожидание другого игрока…");
        }

        var peer = _peerEndpoint;
        if (peer is null)
        {
            accumulatedTime = 0;
            return;
        }
        if (now - _lastPeerSeen > NetworkConstants.PeerIdleTimeout)
        {
            (actions.SocketToClose, actions.StopToClose, _) = EndHostMatchLocked(
                "Связь потеряна. Ожидание второго игрока…",
                "Связь потеряна. Нажмите «Быстрая игра», чтобы сыграть снова.");
            accumulatedTime = 0;
            return;
        }

        actions.Destination = peer;
        var localAxis = _controllers.GetAxis(now);
        // PeriodicTimer coalesces missed wakes; catch up a bounded number of fixed physics steps.
        accumulatedTime = Math.Min(accumulatedTime + elapsed,
            GameConstants.FixedStepSeconds * NetworkConstants.MaximumSimulationCatchUpSteps);
        for (var step = 0; step < NetworkConstants.MaximumSimulationCatchUpSteps &&
                           accumulatedTime >= GameConstants.FixedStepSeconds; step++)
        {
            _lastHostAxis = localAxis;
            _hostTimeline.Advance(localAxis);
            accumulatedTime -= GameConstants.FixedStepSeconds;
        }
        if (_game.TickNumber - _lastStateSentTick >= NetworkConstants.StateSendIntervalTicks)
        {
            actions.Packet = CreateStatePacket();
            _lastStateSentTick = _game.TickNumber;
        }
    }

    // Called under _gate. A timed-out challenge still sends Cancel before closing its socket.
    private void TickGuestLocked(DateTime now, double elapsed, ref double accumulatedTime, ref ClockActions actions)
    {
        actions.Destination = _targetEndpoint;
        if (_connection == ConnectionState.Connected && now - _lastPeerSeen > NetworkConstants.PeerIdleTimeout)
        {
            (actions.SocketToClose, actions.StopToClose, _) = ResetSocketLocked(
                "Связь потеряна. Подключитесь к игре заново.");
            actions.Socket = null;
            actions.Destination = null;
            accumulatedTime = 0;
            return;
        }
        if ((_connection == ConnectionState.Connecting &&
             now - _challengeStartedAt >= NetworkConstants.ChallengeConnectTimeout) ||
            (_connection == ConnectionState.AwaitingAcceptance &&
             (now - _challengeStartedAt >= NetworkConstants.ChallengeLifetime ||
              now - _lastPeerSeen >= NetworkConstants.PeerIdleTimeout)))
        {
            if (_outgoingChallengeId is { } challengeId)
                actions.Packet = new CancelChallengePacket { RequestId = challengeId };
            (actions.SocketToClose, actions.StopToClose, _) = ResetSocketLocked(
                "Вызов истёк или связь с соперником потеряна.");
            accumulatedTime = 0;
            return;
        }
        if (_connection is ConnectionState.Connecting or ConnectionState.AwaitingAcceptance)
        {
            if (now - _lastHelloSent >= NetworkConstants.HelloRetryInterval &&
                _outgoingChallengeId is { } challengeId)
            {
                _lastHelloSent = now;
                actions.Packet = new HelloPacket { RequestId = challengeId, Nickname = _localNickname };
            }
            accumulatedTime = 0;
            return;
        }
        if (_connection != ConnectionState.Connected || _sessionId is not { } sessionId)
        {
            accumulatedTime = 0;
            return;
        }

        if (_pendingRestartRequestId is { } restartId &&
            now - _lastRestartSent >= NetworkConstants.RestartRetryInterval)
        {
            _lastRestartSent = now;
            actions.Packet = new RestartPacket { SessionId = sessionId, RequestId = restartId };
        }
        if (!_guestTimeline.Started)
        {
            accumulatedTime = 0;
            return;
        }

        var axis = _controllers.GetAxis(now);
        accumulatedTime = Math.Min(accumulatedTime + elapsed,
            GameConstants.FixedStepSeconds * NetworkConstants.MaximumSimulationCatchUpSteps);
        for (var step = 0; step < NetworkConstants.MaximumSimulationCatchUpSteps &&
                           accumulatedTime >= GameConstants.FixedStepSeconds; step++)
        {
            _guestTimeline.Advance(axis);
            accumulatedTime -= GameConstants.FixedStepSeconds;
        }
        if (_game.TickNumber > _lastInputSentTick && _guestTimeline.HasCurrentInput)
        {
            actions.Input = _guestTimeline.CreateInputPacket(sessionId, ++_outSequence);
            _lastInputSentTick = _game.TickNumber;
        }
    }

    private StatePacket CreateStatePacket()
    {
        var state = _game.Capture();
        return new StatePacket
        {
            SessionId = _sessionId!.Value, Sequence = state.TickNumber,
            LeftY = state.LeftY, RightY = state.RightY,
            BallX = state.BallX, BallY = state.BallY,
            BallVx = state.BallVx, BallVy = state.BallVy,
            LeftScore = state.LeftScore, RightScore = state.RightScore,
            Phase = state.Phase, Countdown = state.Countdown, RoundId = state.RoundId,
            ServeDirection = state.ServeDirection, Hits = state.Hits, HostAxis = _lastHostAxis,
            RecentEvents = state.RecentEvents.ToArray(),
            LastEventTick = state.LastEventTick, EventOrdinal = state.EventOrdinal
        };
    }
}
