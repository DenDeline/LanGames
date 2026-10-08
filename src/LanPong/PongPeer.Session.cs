using System.Net.Sockets;

namespace LanPong;

internal sealed partial class PongPeer
{
    private async Task<bool> StopSocketAsync(Func<bool>? canStop = null)
    {
        (UdpClient? Socket, CancellationTokenSource? Stop, Task? Receiver) detached;
        PreparedBotSession? bot;
        lock (_gate)
        {
            if (canStop is not null && !canStop()) return false;
            bot = _botSession;
            _botSession = null;
            UpdateBotIdentityLocked();
            detached = ResetSocketLocked();
        }
        // Native cleanup never blocks snapshots or the simulation state lock.
        bot?.Dispose();
        _mdns.SetHostPort(null);
        detached.Stop?.Cancel();
        detached.Socket?.Dispose();
        if (detached.Receiver is not null)
        {
            try { await detached.Receiver; }
            catch (OperationCanceledException) { }
        }
        if (detached.Receiver is null) detached.Stop?.Dispose();
        return true;
    }

    // The receive loop may call this without awaiting its own completion.
    private (UdpClient? Socket, CancellationTokenSource? Stop, Task? Receiver) ResetSocketLocked(
        string message = "Выберите бота или сыграйте с другом по локальной сети.")
    {
        var retryQuick = _quickMode && _role == PeerRole.Guest &&
                         _connection is (ConnectionState.Connecting or ConnectionState.AwaitingAcceptance);
        var detached = (_socket, _socketStop, _receiveTask);
        _socket = null;
        _quickHostAutoAccept = false;
        _socketStop = null;
        _receiveTask = null;
        _peerEndpoint = _targetEndpoint = null;
        _incomingChallengeEndpoint = null;
        _peerNickname = null;
        _peerSocketAddress = _targetSocketAddress = null;
        _incomingChallengeSocketAddress = null;
        _sessionId = _lastRestartRequestId = _pendingRestartRequestId = null;
        _outgoingChallengeId = _incomingChallengeId = _acceptedChallengeId = null;
        _rejectedChallenges.Clear();
        _confirmedGuestEvents = [];
        _restartAfterRound = 0;
        _role = PeerRole.None;
        _connection = retryQuick ? ConnectionState.Searching : ConnectionState.Idle;
        _message = retryQuick ? "Соперник недоступен. Ищем другую игру…" : message;
        _udpPort = 0;
        _controllers.Clear();
        _outSequence = 0;
        _lastInputSentTick = 0;
        _lastHostAxis = 0;
        _lastStateSequence = -1;
        _lastStateSentTick = 0;
        _lastPeerSeen = _lastHelloSent = _lastRestartSent = DateTime.MinValue;
        _challengeStartedAt = _lastChallengeSeen = DateTime.MinValue;
        _ping.Reset();
        _game.ResetWaiting();
        _hostTimeline.Reset();
        _guestTimeline.Reset();
        return detached;
    }

    private static void CloseDetachedSocket(UdpClient? socket, CancellationTokenSource? stop)
    {
        stop?.Cancel();
        socket?.Dispose();
    }

    // Preserve rejected IDs so delayed/retried Hello packets cannot reopen those challenges.
    private void ClearIncomingChallengeLocked(string message)
    {
        if (_incomingChallengeSocketAddress is not null && _incomingChallengeId is not null)
            _rejectedChallenges.Remember(
                _incomingChallengeSocketAddress, _incomingChallengeId.Value, DateTime.UtcNow);
        _incomingChallengeEndpoint = null;
        _peerNickname = null;
        _incomingChallengeSocketAddress = null;
        _incomingChallengeId = null;
        _challengeStartedAt = _lastChallengeSeen = DateTime.MinValue;
        _connection = ConnectionState.Waiting;
        _message = message;
        _mdns.SetHostPort(_udpPort);
    }

    private void ReturnHostToWaitingLocked(string message)
    {
        _peerEndpoint = null;
        _peerNickname = null;
        _peerSocketAddress = null;
        _sessionId = null;
        _acceptedChallengeId = null;
        _lastRestartRequestId = null;
        _lastPeerSeen = DateTime.MinValue;
        _connection = ConnectionState.Waiting;
        _message = message;
        _mdns.SetHostPort(_udpPort);
        _lastHostAxis = 0;
        _lastStateSentTick = 0;
        _ping.Reset();
        _game.ResetWaiting();
        _hostTimeline.Reset();
    }

    // A Quick Game lobby serves one match; an explicitly hosted lobby remains open.
    private (UdpClient? Socket, CancellationTokenSource? Stop, Task? Receiver) EndHostMatchLocked(
        string waitingMessage, string quickMessage)
    {
        if (!_quickHostAutoAccept)
        {
            ReturnHostToWaitingLocked(waitingMessage);
            return default;
        }

        var detached = ResetSocketLocked(quickMessage);
        _mdns.SetHostPort(null);
        return detached;
    }

}
