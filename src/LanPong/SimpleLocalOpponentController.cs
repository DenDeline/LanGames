using static LanPong.GameConstants;

namespace LanPong;

/// <summary>A sampled, short-lookahead tracker for the right paddle.</summary>
internal sealed class SimpleLocalOpponentController : ILocalOpponentController
{
    internal const int ObservationIntervalTicks = 9; // 150 ms at the authoritative 60 Hz tick.
    internal const double ObservationActivationX = 0.72;
    internal const double LookAheadSeconds = 0.25;
    internal const double TargetDeadZone = 0.018;

    private readonly TrackerBotSettings _settings;
    private long _nextObservationTick;
    private double _targetY = ArenaCenter;

    internal SimpleLocalOpponentController()
        : this(new TrackerBotSettings(ObservationIntervalTicks, ObservationActivationX,
            LookAheadSeconds, TargetDeadZone)) { }

    internal SimpleLocalOpponentController(TrackerBotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public void Reset()
    {
        _nextObservationTick = 0;
        _targetY = ArenaCenter;
    }

    public int GetAxis(GameState state)
    {
        if (state.Phase is GamePhase.Waiting or GamePhase.GameOver) return 0;

        if (state.Phase == GamePhase.Countdown)
        {
            _targetY = ArenaCenter;
            _nextObservationTick = state.TickNumber;
        }
        else if (state.TickNumber >= _nextObservationTick)
        {
            _targetY = ObserveTarget(state);
            _nextObservationTick = state.TickNumber + _settings.ObservationIntervalTicks;
        }

        var distance = _targetY - state.RightY;
        return distance > _settings.TargetDeadZone ? 1 : distance < -_settings.TargetDeadZone ? -1 : 0;
    }

    private double ObserveTarget(GameState state)
    {
        // This pilot reacts only once the ball is in the right-hand approach.
        // The stronger teacher can prepare for the full flight instead.
        if (state.BallVx <= 0 || state.BallX < _settings.ObservationActivationX ||
            state.BallX >= RightContactX)
            return ArenaCenter;

        // Only look a short fixed time ahead. The contact plane caps that horizon
        // near the paddle, but this policy does not plan the full ball flight.
        var timeToPaddle = (RightContactX - state.BallX) / state.BallVx;
        var horizon = Math.Min(timeToPaddle, _settings.LookAheadSeconds);
        var projectedY = state.BallY + state.BallVy * horizon;
        return Math.Clamp(ReflectFromWalls(projectedY), MinPaddleY, MaxPaddleY);
    }

    private static double ReflectFromWalls(double y)
    {
        var height = BottomContactY - TopContactY;
        var period = height * 2;
        var offset = (y - TopContactY) % period;
        if (offset < 0) offset += period;
        return TopContactY + (offset <= height ? offset : period - offset);
    }
}
