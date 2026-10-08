namespace LanPong;

/// <summary>
/// Keeps the authoritative simulation's recent pre-tick states and inputs. A guest
/// input arriving for an already simulated tick replaces the prediction and replays
/// every affected tick before another state is published.
/// </summary>
internal sealed class HostRollbackTimeline(GameEngine game)
{
    private readonly Dictionary<long, GameState> _before = [];
    private readonly Dictionary<long, int> _localAxes = [];
    private readonly Dictionary<long, RemoteInput> _receivedAxes = [];
    private readonly Dictionary<long, AppliedRemoteInput> _appliedAxes = [];
    private PaddleSide _hostSide;
    private bool _initialized;

    public void Reset(PaddleSide hostSide)
    {
        PaddleSides.Validate(hostSide);
        _hostSide = hostSide;
        _initialized = true;
        _before.Clear();
        _localAxes.Clear();
        _receivedAxes.Clear();
        _appliedAxes.Clear();
        _appliedAxes[game.TickNumber] = new AppliedRemoteInput(0, -1);
    }

    public void Advance(int localAxis)
    {
        EnsureInitialized();
        var tick = game.TickNumber + 1;
        _before[tick] = game.CaptureCheckpoint();
        _localAxes[tick] = localAxis;
        var previous = _appliedAxes[tick - 1];
        var remote = ResolveRemote(tick, previous);
        game.AdvanceForSide(GameConstants.FixedStepSeconds, _hostSide, localAxis, remote.Axis);
        _appliedAxes[tick] = remote;
        Prune(tick);
    }

    /// <returns>True when replay changed the current authoritative state.</returns>
    public bool Receive(InputPacket packet)
    {
        EnsureInitialized();
        if (packet.Axes is not { Length: >= 1 and <= NetworkConstants.InputRedundancyTicks } axes ||
            axes.Any(axis => axis is < -1 or > 1)) return false;
        var currentTick = game.TickNumber;
        var earliest = long.MaxValue;
        for (var i = 0; i < axes.Length; i++)
        {
            var tick = packet.Tick - i;
            if (tick <= 0 || tick < currentTick - NetworkConstants.RollbackHistoryTicks + 1 ||
                tick > currentTick + NetworkConstants.MaximumFutureInputTicks)
                continue;

            if (_receivedAxes.TryGetValue(tick, out var previous))
            {
                if (previous.Sequence >= packet.Sequence) continue;
                _receivedAxes[tick] = new RemoteInput(axes[i], packet.Sequence);
                if (previous.Axis == axes[i]) continue;
            }
            else _receivedAxes[tick] = new RemoteInput(axes[i], packet.Sequence);
            if (tick <= currentTick && _before.ContainsKey(tick)) earliest = Math.Min(earliest, tick);
        }

        if (earliest == long.MaxValue) return false;
        var oldState = game.CaptureCheckpoint();
        Replay(earliest, currentTick);
        return !game.CaptureCheckpoint().Equals(oldState);
    }

    private AppliedRemoteInput ResolveRemote(long tick, AppliedRemoteInput previous)
    {
        if (_receivedAxes.TryGetValue(tick, out var received))
            return new AppliedRemoteInput(received.Axis, tick);
        return tick - previous.LastExactTick <= NetworkConstants.RemoteInputStaleTicks
            ? previous
            : new AppliedRemoteInput(0, previous.LastExactTick);
    }

    private void Replay(long fromTick, long throughTick)
    {
        game.RestoreCheckpoint(_before[fromTick]);
        for (var tick = fromTick; tick <= throughTick; tick++)
        {
            _before[tick] = game.CaptureCheckpoint();
            var remote = ResolveRemote(tick, _appliedAxes[tick - 1]);
            game.AdvanceForSide(GameConstants.FixedStepSeconds, _hostSide, _localAxes[tick], remote.Axis);
            _appliedAxes[tick] = remote;
        }
    }

    private void Prune(long currentTick)
    {
        // Retain the previous applied input as the replay baseline.
        var floor = currentTick - NetworkConstants.RollbackHistoryTicks;
        _before.Remove(floor);
        _localAxes.Remove(floor);
        _receivedAxes.Remove(floor);
        _appliedAxes.Remove(floor - 1);
    }

    private readonly record struct RemoteInput(int Axis, long Sequence);
    private readonly record struct AppliedRemoteInput(int Axis, long LastExactTick);

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("Reset the host timeline with its resolved paddle side first.");
    }
}
