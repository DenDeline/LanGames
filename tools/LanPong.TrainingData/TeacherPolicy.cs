using LanPong;
using static LanPong.GameConstants;

namespace LanPong.TrainingData;

/// <summary>
/// Offline right-paddle teacher. It observes only the current authoritative game
/// state and plans with counterfactual copies of the production physics engine.
/// </summary>
internal sealed class TeacherPolicy : ILocalOpponentController
{
    // The full flight between paddles is under three seconds at every legal
    // bounce speed. Keep the search bounded even for unusual supplied states.
    internal const int MaximumRolloutTicks = 300;
    internal const int DecisionIntervalTicks = 9;
    // A chosen axis is held for the whole decision block. Do not command a
    // 0.1275-height move for an error smaller than half of that distance.
    internal const double TargetDeadZone =
        PaddleSpeed * FixedStepSeconds * DecisionIntervalTicks / 2;

    private static readonly double[] AimOffsets =
        [0, 0.04, -0.04, 0.08, -0.08, 0.09, -0.09];

    private readonly GameEngine _rollout = new();
    private readonly SimpleLocalOpponentController _assumedLeft = new();
    private int _heldAxis;

    public void Reset()
    {
        _heldAxis = 0;
        _assumedLeft.Reset();
    }

    public int GetAxis(GameState state)
    {
        if (state.Phase is GamePhase.Waiting or GamePhase.GameOver)
        {
            _heldAxis = 0;
            return 0;
        }

        // Training rows and the eventual student inference are sampled at the
        // same global tick boundary. A serve between boundaries keeps the
        // previous (typically centering) action until the next decision.
        if (state.TickNumber % DecisionIntervalTicks != 0) return _heldAxis;

        var aim = state.Phase == GamePhase.Playing && state.BallVx > 0 &&
                  state.BallX < RightContactX
            ? ChooseAim(state)
            : ArenaCenter;
        _heldAxis = AxisToward(state.RightY, aim);
        return _heldAxis;
    }

    private double ChooseAim(GameState state)
    {
        var timeToContact = Math.Max(0, (RightContactX - state.BallX) / state.BallVx);
        var contactY = ReflectFromWalls(state.BallY + state.BallVy * timeToContact);
        var checkpoint = state with { RecentEvents = state.RecentEvents ?? GameEventHistory.Empty };

        var bestAim = ArenaCenter;
        var best = default(AimResult);
        var first = true;
        foreach (var offset in AimOffsets)
        {
            var aim = Math.Clamp(contactY + offset, MinPaddleY, MaxPaddleY);
            var candidate = EvaluateAim(checkpoint, aim);
            if (!first && !candidate.BetterThan(best)) continue;
            bestAim = aim;
            best = candidate;
            first = false;
        }

        return bestAim;
    }

    private AimResult EvaluateAim(GameState checkpoint, double aim)
    {
        _rollout.RestoreCheckpoint(checkpoint);
        _assumedLeft.Reset();
        var rightHit = false;
        var lastIncomingLeftY = checkpoint.BallY;
        var rightAxis = 0;

        for (var step = 0; step < MaximumRolloutTicks; step++)
        {
            var before = _rollout.Capture();
            if (before.Phase != GamePhase.Playing) break;

            // Mirroring lets the existing right-paddle tracker stand in for a
            // left opponent. It sees the same observable state, with no access
            // to real future inputs or to the policy used in the actual match.
            var leftAxis = _assumedLeft.GetAxis(Mirror(before));
            if (before.TickNumber % DecisionIntervalTicks == 0)
                rightAxis = AxisToward(before.RightY, rightHit ? ArenaCenter : aim);
            _rollout.Advance(FixedStepSeconds, leftAxis, rightAxis);
            var after = _rollout.Capture();

            if (!rightHit)
            {
                if (after.LeftScore > checkpoint.LeftScore)
                    return new AimResult(false, false, 0);
                if (before.BallVx > 0 && after.BallVx < 0)
                    rightHit = true;
            }
            else
            {
                if (before.BallVx < 0) lastIncomingLeftY = before.BallY;
                if (after.RightScore > checkpoint.RightScore)
                    return new AimResult(true, true,
                        Math.Abs(lastIncomingLeftY - checkpoint.LeftY));
                if (before.BallVx < 0 && after.BallVx > 0)
                    return new AimResult(true, false,
                        Math.Abs(after.BallY - checkpoint.LeftY));
            }
        }

        // A bounded search never fabricates a win. A save whose return has not
        // reached the left paddle still outranks every right-paddle miss.
        return new AimResult(rightHit, false, 0);
    }

    private static GameState Mirror(GameState state) => state with
    {
        LeftY = state.RightY,
        RightY = state.LeftY,
        BallX = 1 - state.BallX,
        BallVx = -state.BallVx,
        LeftScore = state.RightScore,
        RightScore = state.LeftScore,
        ServeDirection = -state.ServeDirection
    };

    private static int AxisToward(double currentY, double targetY)
    {
        var distance = targetY - currentY;
        return distance > TargetDeadZone ? 1 : distance < -TargetDeadZone ? -1 : 0;
    }

    private static double ReflectFromWalls(double y)
    {
        var height = BottomContactY - TopContactY;
        var period = 2 * height;
        var offset = (y - TopContactY) % period;
        if (offset < 0) offset += period;
        return TopContactY + (offset <= height ? offset : period - offset);
    }

    private readonly record struct AimResult(bool RightHit, bool RightGoal, double LeftDisplacement)
    {
        public bool BetterThan(AimResult other)
        {
            if (RightHit != other.RightHit) return RightHit;
            if (RightGoal != other.RightGoal) return RightGoal;
            return LeftDisplacement > other.LeftDisplacement + 1e-9;
        }
    }

}
