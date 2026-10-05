using static LanPong.GameConstants;

namespace LanPong;

/// <summary>A deterministic simulation that can restore and replay prior ticks.</summary>
internal sealed class GameEngine
{
    private GamePhase _phase = GamePhase.Waiting;
    private int _serveDirection = 1;
    private int _hits;
    private long _lastEventTick = -1;
    private int _eventOrdinal;
    private GameEventHistory _recentEvents = GameEventHistory.Empty;

    public double LeftY { get; private set => field = Math.Clamp(value, MinPaddleY, MaxPaddleY); } = ArenaCenter;
    public double RightY { get; private set => field = Math.Clamp(value, MinPaddleY, MaxPaddleY); } = ArenaCenter;
    public double BallX { get; private set; } = ArenaCenter;
    public double BallY { get; private set; } = ArenaCenter;
    public double BallVx { get; private set; }
    public double BallVy { get; private set; }
    public int LeftScore { get; private set; }
    public int RightScore { get; private set; }
    public GamePhase Phase => _phase;
    public double Countdown { get; private set; }
    public long TickNumber { get; private set; }
    public int RoundId { get; private set; }
    public GameEvent[] RecentEvents => _recentEvents.ToArray();

    public GameState Capture() => new()
    {
        LeftY = LeftY, RightY = RightY,
        BallX = BallX, BallY = BallY,
        BallVx = BallVx, BallVy = BallVy,
        LeftScore = LeftScore, RightScore = RightScore,
        Phase = _phase, Countdown = Countdown,
        TickNumber = TickNumber, RoundId = RoundId,
        ServeDirection = _serveDirection, Hits = _hits,
        LastEventTick = _lastEventTick, EventOrdinal = _eventOrdinal,
        RecentEvents = _recentEvents
    };

    /// <summary>Capture every value needed to replay the simulation from this tick.</summary>
    public GameState CaptureCheckpoint() => Capture();

    /// <summary>Restore a locally captured checkpoint without changing its values.</summary>
    public void RestoreCheckpoint(GameState checkpoint)
    {
        LeftY = checkpoint.LeftY;
        RightY = checkpoint.RightY;
        BallX = checkpoint.BallX;
        BallY = checkpoint.BallY;
        BallVx = checkpoint.BallVx;
        BallVy = checkpoint.BallVy;
        LeftScore = checkpoint.LeftScore;
        RightScore = checkpoint.RightScore;
        _phase = checkpoint.Phase;
        Countdown = checkpoint.Countdown;
        TickNumber = checkpoint.TickNumber;
        RoundId = checkpoint.RoundId;
        _serveDirection = checkpoint.ServeDirection;
        _hits = checkpoint.Hits;
        _lastEventTick = checkpoint.LastEventTick;
        _eventOrdinal = checkpoint.EventOrdinal;
        _recentEvents = checkpoint.RecentEvents;
    }

    public void ResetWaiting()
    {
        LeftY = RightY = BallX = BallY = ArenaCenter;
        BallVx = BallVy = 0;
        LeftScore = RightScore = 0;
        Countdown = 0;
        TickNumber = 0;
        _phase = GamePhase.Waiting;
        _serveDirection = 1;
        _hits = 0;
        _lastEventTick = -1;
        _eventOrdinal = 0;
        _recentEvents = GameEventHistory.Empty;
    }

    public void StartMatch()
    {
        LeftY = RightY = ArenaCenter;
        LeftScore = RightScore = 0;
        RoundId++;
        _lastEventTick = -1;
        _eventOrdinal = 0;
        _recentEvents = GameEventHistory.Empty;
        StartRound(1);
        RecordEvent(GameEventKind.MatchStart, ArenaCenter, ArenaCenter);
    }

    private void StartRound(int direction)
    {
        _serveDirection = direction;
        _hits = 0;
        BallX = BallY = ArenaCenter;
        BallVx = BallVy = 0;
        Countdown = ServeCountdownSeconds;
        _phase = GamePhase.Countdown;
    }

    public void Advance(double dt, int leftAxis, int rightAxis)
    {
        if (!double.IsFinite(dt) || dt < 0 || dt > MaximumAdvanceSeconds)
            throw new ArgumentOutOfRangeException(nameof(dt),
                $"Step duration must be between zero and {MaximumAdvanceSeconds} seconds.");

        // Ticks continue in terminal states so a lost final UDP snapshot can be resent.
        TickNumber++;
        if (_phase is GamePhase.Waiting or GamePhase.GameOver || dt == 0) return;

        leftAxis = Math.Clamp(leftAxis, -1, 1);
        rightAxis = Math.Clamp(rightAxis, -1, 1);
        var startLeftY = LeftY;
        var startRightY = RightY;

        var elapsed = 0.0;
        while (elapsed < dt)
        {
            if (_phase == GamePhase.Countdown)
            {
                var countTime = Math.Min(Countdown, dt - elapsed);
                Countdown -= countTime;
                elapsed += countTime;
                if (Countdown > 0) break;

                Countdown = 0;
                _phase = GamePhase.Playing;
                BallVx = ServeSpeedX * _serveDirection;
                BallVy = ServeSpeedY * _serveDirection;
                RecordEvent(GameEventKind.Serve, BallX, BallY);
                continue;
            }

            if (_phase != GamePhase.Playing) break;
            var result = AdvanceBall(dt - elapsed, elapsed, startLeftY, startRightY, leftAxis, rightAxis);
            elapsed += result.TimeUsed;
            if (result.Goal == Goal.None) break;

            if (result.Goal == Goal.Left) RightScore++;
            else LeftScore++;
            RecordEvent(GameEventKind.Goal, result.Goal == Goal.Left ? 0 : 1, BallY);
            AfterPoint(result.Goal == Goal.Left ? -1 : 1);
            if (_phase == GamePhase.GameOver) break;
        }

        // A winning goal ends paddle movement at the goal time, even inside a step.
        LeftY = PaddleAt(startLeftY, leftAxis, elapsed);
        RightY = PaddleAt(startRightY, rightAxis, elapsed);
    }

    private BallStep AdvanceBall(
        double duration, double stepOffset, double startLeftY, double startRightY,
        int leftAxis, int rightAxis)
    {
        var remaining = duration;
        var elapsed = stepOffset;

        while (remaining > 0)
        {
            var contact = FindFirstContact(remaining);
            if (contact.Kind == Contact.None)
            {
                MoveBall(remaining);
                return new BallStep(duration, Goal.None);
            }

            MoveBall(contact.Time);
            elapsed += contact.Time;
            remaining -= contact.Time;
            switch (contact.Kind)
            {
                case Contact.Top:
                    BallY = TopContactY;
                    BallVy = -BallVy;
                    RecordEvent(GameEventKind.Wall, BallX, BallY);
                    break;
                case Contact.Bottom:
                    BallY = BottomContactY;
                    BallVy = -BallVy;
                    RecordEvent(GameEventKind.Wall, BallX, BallY);
                    break;
                case Contact.LeftPaddle:
                    var leftAtContact = PaddleAt(startLeftY, leftAxis, elapsed);
                    if (Math.Abs(BallY - leftAtContact) <= PaddleHalfHeight + BallRadiusY)
                    {
                        BallX = LeftContactX;
                        Bounce(leftAtContact, 1);
                        RecordEvent(GameEventKind.Paddle, BallX, BallY);
                    }
                    else BallX = LeftContactX - MissSeparation;
                    break;
                case Contact.RightPaddle:
                    var rightAtContact = PaddleAt(startRightY, rightAxis, elapsed);
                    if (Math.Abs(BallY - rightAtContact) <= PaddleHalfHeight + BallRadiusY)
                    {
                        BallX = RightContactX;
                        Bounce(rightAtContact, -1);
                        RecordEvent(GameEventKind.Paddle, BallX, BallY);
                    }
                    else BallX = RightContactX + MissSeparation;
                    break;
                case Contact.LeftGoal:
                    BallX = -BallRadiusX;
                    return new BallStep(duration - remaining, Goal.Left);
                case Contact.RightGoal:
                    BallX = 1 + BallRadiusX;
                    return new BallStep(duration - remaining, Goal.Right);
            }
        }

        return new BallStep(duration, Goal.None);
    }

    private Collision FindFirstContact(double remaining)
    {
        var first = new Collision(Contact.None, double.PositiveInfinity);
        if (BallVy < 0)
            first = Earlier(first, Contact.Top, (TopContactY - BallY) / BallVy, remaining);
        else if (BallVy > 0)
            first = Earlier(first, Contact.Bottom, (BottomContactY - BallY) / BallVy, remaining);

        if (BallVx < 0)
        {
            if (BallX >= LeftContactX - ContactTolerance)
                first = Earlier(first, Contact.LeftPaddle, (LeftContactX - BallX) / BallVx, remaining);
            first = Earlier(first, Contact.LeftGoal, (-BallRadiusX - BallX) / BallVx, remaining);
        }
        else if (BallVx > 0)
        {
            if (BallX <= RightContactX + ContactTolerance)
                first = Earlier(first, Contact.RightPaddle, (RightContactX - BallX) / BallVx, remaining);
            first = Earlier(first, Contact.RightGoal, (1 + BallRadiusX - BallX) / BallVx, remaining);
        }
        return first;
    }

    private static Collision Earlier(Collision first, Contact kind, double time, double remaining)
    {
        if (time < -ContactTolerance || time > remaining + ContactTolerance) return first;
        time = Math.Clamp(time, 0, remaining);
        return time < first.Time ? new Collision(kind, time) : first;
    }

    private void MoveBall(double time)
    {
        BallX += BallVx * time;
        BallY += BallVy * time;
    }

    private static double PaddleAt(double startY, int axis, double elapsed) =>
        Math.Clamp(startY + axis * PaddleSpeed * elapsed, MinPaddleY, MaxPaddleY);

    private void Bounce(double paddleY, int direction)
    {
        _hits++;
        var launchSpeed = Math.Min(ServeSpeedX + _hits * BounceSpeedIncrease, MaximumBounceSpeed);
        var angle = Math.Clamp((BallY - paddleY) / PaddleHalfHeight, -1, 1) * MaximumBounceAngle;
        BallVx = direction * launchSpeed * Math.Cos(angle);
        // Dampen the vertical component deliberately to keep off-center rallies playable.
        BallVy = launchSpeed * Math.Sin(angle) * VerticalBounceScale;
    }

    private void AfterPoint(int direction)
    {
        if (LeftScore >= WinningScore || RightScore >= WinningScore)
        {
            _phase = GamePhase.GameOver;
            BallVx = BallVy = 0;
            Countdown = 0;
        }
        else StartRound(direction);
    }

    private void RecordEvent(GameEventKind kind, double x, double y)
    {
        if (_lastEventTick != TickNumber)
        {
            _lastEventTick = TickNumber;
            _eventOrdinal = 0;
        }
        // Tick, event order, and kind are part of the id, so replay cannot reuse
        // an already observed id for a different kind or tick.
        var id = $"{TickNumber}:{_eventOrdinal++}:{(int)kind}";
        _recentEvents = _recentEvents.Append(new GameEvent(id, kind, TickNumber, x, y));
    }

    /// <summary>Apply an untrusted network state to the local simulation.</summary>
    public void Restore(GameState state)
    {
        LeftY = FiniteClamp(state.LeftY, MinPaddleY, MaxPaddleY, ArenaCenter);
        RightY = FiniteClamp(state.RightY, MinPaddleY, MaxPaddleY, ArenaCenter);
        BallX = FiniteClamp(state.BallX, -BallRadiusX, 1 + BallRadiusX, ArenaCenter);
        BallY = FiniteClamp(state.BallY, TopContactY, BottomContactY, ArenaCenter);
        BallVx = FiniteClamp(state.BallVx, -MaximumReplicaBallSpeed, MaximumReplicaBallSpeed, 0);
        BallVy = FiniteClamp(state.BallVy, -MaximumReplicaBallSpeed, MaximumReplicaBallSpeed, 0);
        LeftScore = Math.Max(0, state.LeftScore);
        RightScore = Math.Max(0, state.RightScore);
        _phase = state.Phase is >= GamePhase.Waiting and <= GamePhase.GameOver ? state.Phase : GamePhase.Waiting;
        Countdown = FiniteClamp(state.Countdown, 0, MaximumReplicaCountdown, 0);
        TickNumber = Math.Max(0, state.TickNumber);
        RoundId = Math.Max(0, state.RoundId);
        _serveDirection = state.ServeDirection is -1 or 1 ? state.ServeDirection : 1;
        _hits = Math.Max(0, state.Hits);
        _lastEventTick = Math.Clamp(state.LastEventTick, -1, TickNumber);
        _eventOrdinal = Math.Max(0, state.EventOrdinal);
        _recentEvents = state.RecentEvents ?? GameEventHistory.Empty;
    }

    private static double FiniteClamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private enum Contact { None, Top, Bottom, LeftPaddle, RightPaddle, LeftGoal, RightGoal }
    private enum Goal { None, Left, Right }
    private readonly record struct Collision(Contact Kind, double Time);
    private readonly record struct BallStep(double TimeUsed, Goal Goal);
}
