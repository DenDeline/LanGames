using LanPong;
using static LanPong.GameConstants;

namespace LanPong.TrainingData;

/// <summary>Seeded, legal left-paddle behavior used only for headless trajectories.</summary>
internal sealed class LeftOpponentPolicy(LeftPolicyProfile profile, ulong seed)
{
    private long _nextObservationTick;
    private double _targetY = ArenaCenter;
    private double _openingY;
    private int _lapsePhase;

    public string Name => profile.Name;

    public void Reset()
    {
        var random = new StableRandom(seed);
        _openingY = Math.Clamp(ArenaCenter + (random.NextUnit() - 0.5) * 0.24,
            profile.TrackMinY, profile.TrackMaxY);
        _lapsePhase = (int)(random.NextUInt64() % (ulong)profile.LapsePeriodTicks);
        _targetY = _openingY;
        _nextObservationTick = 0;
    }

    public int GetAxis(GameState state)
    {
        if (state.Phase is GamePhase.Waiting or GamePhase.GameOver) return 0;
        if (state.Phase == GamePhase.Countdown)
        {
            _targetY = _openingY;
            _nextObservationTick = state.TickNumber;
        }
        else if (state.BallVx < 0 && state.BallX <= profile.ActivationX &&
                 (state.TickNumber + _lapsePhase) % profile.LapsePeriodTicks <
                 profile.LapseDurationTicks)
        {
            // Brief seeded reaction lapses are legal paddle inputs. Without
            // them two accurate trackers can rally forever in this engine.
            _targetY = state.BallY < ArenaCenter ? MaxPaddleY : MinPaddleY;
            _nextObservationTick = state.TickNumber;
        }
        else if (state.TickNumber >= _nextObservationTick)
        {
            var target = ArenaCenter;
            if (state.BallVx < 0 && state.BallX >= LeftContactX &&
                state.BallX <= profile.ActivationX)
            {
                var timeToContact = (state.BallX - LeftContactX) / -state.BallVx;
                var horizon = Math.Min(timeToContact, profile.LookAheadSeconds);
                target = ReflectFromWalls(state.BallY + state.BallVy * horizon);
                // Contact off center creates legal angled returns. Aim away
                // from the visible right paddle, with a seeded tie at center.
                var tieRandom = new StableRandom(StableRandom.DeriveSeed(seed, 11,
                    checked((int)(state.TickNumber / 480))));
                var seededSign = tieRandom.NextUnit() < 0.5 ? -1 : 1;
                var attackSign = state.RightY > ArenaCenter + 0.025 ? 1 :
                    state.RightY < ArenaCenter - 0.025 ? -1 : seededSign;
                target += attackSign * profile.OffensiveOffset;
            }
            // The stochastic disturbance is indexed by absolute tick, not
            // a sequence consumed by visits. A paired Teacher/Simple scenario
            // receives the same exogenous noise at the same game time.
            var noise = new StableRandom(StableRandom.DeriveSeed(seed, 10,
                checked((int)state.TickNumber))).NextUnit();
            _targetY = Math.Clamp(target + (noise * 2 - 1) * profile.Jitter,
                profile.TrackMinY, profile.TrackMaxY);
            _nextObservationTick = state.TickNumber + profile.ObservationTicks;
        }

        var error = _targetY - state.LeftY;
        return error > 0.018 ? 1 : error < -0.018 ? -1 : 0;
    }

    private static double ReflectFromWalls(double y)
    {
        var height = BottomContactY - TopContactY;
        var period = 2 * height;
        var offset = (y - TopContactY) % period;
        if (offset < 0) offset += period;
        return TopContactY + (offset <= height ? offset : period - offset);
    }
}

internal sealed record LeftPolicyProfile(string Name, int ObservationTicks,
    double LookAheadSeconds, double Jitter, double OffensiveOffset, double ActivationX,
    int LapsePeriodTicks, int LapseDurationTicks, double TrackMinY, double TrackMaxY)
{
    public static readonly LeftPolicyProfile[] All =
    [
        new("delayed-tracker", 15, 0.18, 0.10, 0.05, 1.0, 600, 90,
            MinPaddleY, MaxPaddleY),
        new("sampled-tracker", 9, 0.45, 0.07, 0.065, 1.0, 780, 110,
            MinPaddleY, MaxPaddleY),
        new("contact-tracker", 5, 3.0, 0.05, 0.075, 1.0, 1200, 80,
            MinPaddleY, MaxPaddleY),
        new("late-counterpunch", 5, 3.0, 0.025, 0.085, 0.48, 1200, 60,
            MinPaddleY, MaxPaddleY),
        new("limited-range-counter", 5, 3.0, 0.02, 0.085, 1.0, 1200, 0,
            0.36, 0.64)
    ];

    public static LeftPolicyProfile ForMatch(int matchIndex) => All[matchIndex % All.Length];
}
