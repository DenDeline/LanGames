using System.Net;
using System.Net.Sockets;

namespace LanPong;

internal sealed partial class PongPeer
{
    private static readonly TimeSpan QuickRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FailedHostCooldown = TimeSpan.FromSeconds(15);
    private CancellationTokenSource? _matchmakingStop;
    private Task? _matchmakingTask;
    private bool _quickMode;
    private bool _quickHostAutoAccept;

    public async Task QuickGameAsync(string nickname)
    {
        var selectedNickname = PlayerNickname.Normalize(nickname);
        Task? priorMatchmakingTask;
        lock (_gate)
            priorMatchmakingTask = _role == PeerRole.None && _connection == ConnectionState.Idle &&
                                   !_quickMode ? _matchmakingTask : null;
        // A match can finish before the canceled discovery task reaches its finally
        // block. Let that task release its identity before starting a new search.
        if (priorMatchmakingTask is not null) await priorMatchmakingTask;
        await _transition.WaitAsync();
        try
        {
            lock (_gate)
            {
                if (_stopping) throw new InvalidOperationException("Приложение завершает работу.");
                if (_role != PeerRole.None || _connection != ConnectionState.Idle ||
                    _matchmakingTask is not null)
                    throw new InvalidOperationException("Сначала покиньте текущую игру.");

                _localNickname = selectedNickname;
                _connection = ConnectionState.Searching;
                _message = "Ищем соперника в локальной сети…";
                _quickMode = true;
                var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _matchmakingStop = stop;
                _matchmakingTask = Task.Run(() => RunQuickMatchmakingAsync(stop));
            }
        }
        finally { _transition.Release(); }
    }

    // Called under _gate. The transition or LeaveAsync will close any active socket.
    private void CancelQuickMatchmakingLocked(bool clearLobby = false)
    {
        _quickMode = false;
        if (clearLobby) _quickHostAutoAccept = false;
        _matchmakingStop?.Cancel();
    }

    private bool IsQuickMatchmakingActive(CancellationTokenSource stop)
    {
        lock (_gate)
            return !_stopping && _quickMode && ReferenceEquals(_matchmakingStop, stop);
    }

    private async Task RunQuickMatchmakingAsync(CancellationTokenSource stop)
    {
        var cancellationToken = stop.Token;
        var failedHosts = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        DiscoveredHost? attemptedHost = null;
        try
        {
            while (IsQuickMatchmakingActive(stop))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PeerRole role;
                ConnectionState connection;
                lock (_gate)
                {
                    role = _role;
                    connection = _connection;
                }

                if (connection == ConnectionState.Connected) break;
                if (role == PeerRole.Guest)
                {
                    if (connection is ConnectionState.Connecting or ConnectionState.AwaitingAcceptance)
                    {
                        await Task.Delay(QuickRetryDelay, cancellationToken);
                        continue;
                    }
                    // A declined, timed-out, or withdrawn challenge can be retried without
                    // repeatedly selecting the same stale mDNS advertisement.
                    if (attemptedHost is not null)
                    {
                        failedHosts[HostKey(attemptedHost)] = DateTime.UtcNow + FailedHostCooldown;
                        attemptedHost = null;
                    }
                }
                else if (attemptedHost is not null)
                {
                    failedHosts[HostKey(attemptedHost)] = DateTime.UtcNow + FailedHostCooldown;
                    attemptedHost = null;
                }

                if (role == PeerRole.Host && connection != ConnectionState.Waiting ||
                    role == PeerRole.None && connection != ConnectionState.Searching)
                {
                    await Task.Delay(QuickRetryDelay, cancellationToken);
                    continue;
                }

                var hosts = await _mdns.DiscoverAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsQuickMatchmakingActive(stop)) break;

                var now = DateTime.UtcNow;
                foreach (var key in failedHosts.Where(entry => entry.Value <= now)
                             .Select(entry => entry.Key).ToArray())
                    failedHosts.Remove(key);

                // Both peers can discover an empty network and become hosts at once.
                // Only the host with the greater stable mDNS instance name switches roles.
                var candidates = hosts.Where(host =>
                        !failedHosts.ContainsKey(HostKey(host)) &&
                        (role != PeerRole.Host || string.Compare(host.InstanceName,
                            _mdns.InstanceName, StringComparison.OrdinalIgnoreCase) < 0))
                    .ToArray();
                if (candidates.Length > 0)
                {
                    var selected = candidates[Random.Shared.Next(candidates.Length)];
                    if (await TryJoinQuickHostAsync(selected, stop))
                        attemptedHost = selected;
                    else
                        failedHosts[HostKey(selected)] = DateTime.UtcNow + FailedHostCooldown;
                }
                else if (role == PeerRole.None)
                {
                    await TryHostQuickGameAsync(stop);
                }

                await Task.Delay(QuickRetryDelay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogError(error, "Quick Game matchmaking failed");
            lock (_gate)
            {
                if (ReferenceEquals(_matchmakingStop, stop) && _quickMode)
                {
                    _quickMode = false;
                    if (_role == PeerRole.None)
                    {
                        _connection = ConnectionState.Idle;
                        _message = "Не удалось найти игру. Попробуйте ещё раз.";
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_matchmakingStop, stop))
                {
                    _matchmakingStop = null;
                    _matchmakingTask = null;
                }
            }
            stop.Dispose();
        }
    }

    private async Task<bool> TryHostQuickGameAsync(CancellationTokenSource stop)
    {
        await _transition.WaitAsync(stop.Token);
        try
        {
            string nickname;
            lock (_gate)
            {
                if (!_quickMode || !ReferenceEquals(_matchmakingStop, stop) ||
                    _role != PeerRole.None || _connection != ConnectionState.Searching)
                    return false;
                nickname = _localNickname;
            }
            stop.Token.ThrowIfCancellationRequested();
            if (!await StopSocketAsync(() =>
                    _quickMode && ReferenceEquals(_matchmakingStop, stop) &&
                    _role == PeerRole.None && _connection == ConnectionState.Searching))
                return false;
            stop.Token.ThrowIfCancellationRequested();
            await StartHostingAsync(0, nickname, socketAlreadyStopped: true,
                quickLobby: true);
            return true;
        }
        finally { _transition.Release(); }
    }

    private async Task<bool> TryJoinQuickHostAsync(DiscoveredHost host, CancellationTokenSource stop)
    {
        if (!IPAddress.TryParse(host.Address, out var address)) return false;
        await _transition.WaitAsync(stop.Token);
        try
        {
            string nickname;
            lock (_gate)
            {
                if (!_quickMode || !ReferenceEquals(_matchmakingStop, stop) ||
                    !(_role == PeerRole.None && _connection == ConnectionState.Searching ||
                      _role == PeerRole.Host && _connection == ConnectionState.Waiting &&
                      string.Compare(host.InstanceName, _mdns.InstanceName,
                          StringComparison.OrdinalIgnoreCase) < 0))
                    return false;
                nickname = _localNickname;
            }
            stop.Token.ThrowIfCancellationRequested();
            if (!await StopSocketAsync(() =>
                    _quickMode && ReferenceEquals(_matchmakingStop, stop) &&
                    (_role == PeerRole.None && _connection == ConnectionState.Searching ||
                     _role == PeerRole.Host && _connection == ConnectionState.Waiting &&
                     string.Compare(host.InstanceName, _mdns.InstanceName,
                         StringComparison.OrdinalIgnoreCase) < 0)))
                return false;
            stop.Token.ThrowIfCancellationRequested();
            try
            {
                await StartJoiningAsync(address, host.Port, nickname, stop.Token,
                    socketAlreadyStopped: true);
                return true;
            }
            catch (Exception error) when (error is ArgumentException or SocketException)
            {
                _logger.LogDebug(error, "Quick Game cannot join {InstanceName}", host.InstanceName);
                lock (_gate)
                    if (_quickMode && _role == PeerRole.None)
                    {
                        _connection = ConnectionState.Searching;
                        _message = "Соперник недоступен. Ищем другую игру…";
                    }
                return false;
            }
        }
        finally { _transition.Release(); }
    }

    private static string HostKey(DiscoveredHost host) => $"{host.InstanceName}:{host.Port}";
}
