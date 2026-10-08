namespace LanPong;

/// <summary>
/// Runs the same game locally between authoritative state packets. On each packet it
/// restores the host state and replays locally sampled inputs to its present tick.
/// </summary>
internal sealed class GuestPredictionTimeline(GameEngine game)
{
    private const int MaximumLeadTicks = 8;
    private const int MaximumExtraLeadTicks = 4;
    private const int InputHistoryTicks = 120;
    private readonly Dictionary<long, int> _localAxes = [];
    private int _hostAxis;
    private PaddleSide _hostSide;
    private bool _initialized;

    public bool Started { get; private set; }
    public bool HasCurrentInput => _localAxes.ContainsKey(game.TickNumber);

    public void Reset(PaddleSide hostSide)
    {
        PaddleSides.Validate(hostSide);
        _hostSide = hostSide;
        _initialized = true;
        Started = false;
        _localAxes.Clear();
        _hostAxis = 0;
    }

    public void Advance(int localAxis)
    {
        EnsureInitialized();
        if (!Started) return;
        var tick = game.TickNumber + 1;
        _localAxes[tick] = localAxis;
        game.AdvanceForSide(GameConstants.FixedStepSeconds, _hostSide, _hostAxis, localAxis);
        _localAxes.Remove(tick - InputHistoryTicks - 1);
    }

    public void Reconcile(GameState authoritative, int hostAxis, double? pingMs, int localAxis)
    {
        EnsureInitialized();
        var sameRound = Started && authoritative.RoundId == game.RoundId;
        var presentTick = sameRound ? game.TickNumber : authoritative.TickNumber;
        if (!sameRound || presentTick - authoritative.TickNumber > InputHistoryTicks)
            _localAxes.Clear();

        game.Restore(authoritative);
        _hostAxis = hostAxis;
        Started = true;

        var leadTicks = pingMs is > 0
            ? Math.Clamp((int)Math.Ceiling(pingMs.Value / (2 * 1000 * GameConstants.FixedStepSeconds)), 0,
                MaximumLeadTicks)
            : 0;
        // A delayed host clock cannot be allowed to leave this process arbitrarily
        // far ahead: those future-stamped inputs would be rejected by the host.
        var estimatedHostTick = authoritative.TickNumber + leadTicks;
        var targetTick = Math.Max(estimatedHostTick,
            Math.Min(presentTick, estimatedHostTick + MaximumExtraLeadTicks));
        if (targetTick < presentTick)
        {
            // These speculative ticks are now in the future again. Resample their
            // inputs when they actually occur, including key releases during a stall.
            foreach (var future in _localAxes.Keys.Where(key => key > targetTick).ToArray())
                _localAxes.Remove(future);
        }
        for (var tick = authoritative.TickNumber + 1; tick <= targetTick; tick++)
        {
            if (!_localAxes.TryGetValue(tick, out var axis))
                _localAxes[tick] = axis = localAxis;
            game.AdvanceForSide(GameConstants.FixedStepSeconds, _hostSide, _hostAxis, axis);
        }
        foreach (var old in _localAxes.Keys.Where(key => key < targetTick - InputHistoryTicks).ToArray())
            _localAxes.Remove(old);
    }

    public InputPacket CreateInputPacket(Guid sessionId, long sequence)
    {
        EnsureInitialized();
        var tick = game.TickNumber;
        var axes = new List<int>(NetworkConstants.InputRedundancyTicks);
        for (var i = 0; i < NetworkConstants.InputRedundancyTicks && tick - i > 0; i++)
        {
            if (!_localAxes.TryGetValue(tick - i, out var axis)) break;
            axes.Add(axis);
        }
        if (axes.Count == 0) throw new InvalidOperationException("No local input was sampled for this tick.");

        return new InputPacket
        {
            SessionId = sessionId,
            Sequence = sequence,
            Tick = tick,
            RoundId = game.RoundId,
            Axes = [.. axes]
        };
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("Reset the guest timeline with the resolved host paddle side first.");
    }
}
