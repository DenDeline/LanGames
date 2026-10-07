using LanPong.TrainingData;

namespace LanPong.Tests;

public sealed class TeacherPolicyTests
{
    [Test]
    public async Task GetAxis_UsesWallReflectedContactInsteadOfFollowingCurrentBallHeight()
    {
        var state = Playing(ballX: 0.5, ballY: 0.96, vx: 0.55, vy: 0.4,
            rightY: 0.9);
        var teacher = new TeacherPolicy();

        // The ball starts at the bottom, but it reaches the right paddle after
        // a wall reflection near y=0.70. The correct first move is upward.
        await Assert.That(teacher.GetAxis(state)).IsEqualTo(-1);
    }

    [Test]
    public async Task GetAxis_HoldsItsDecisionUntilTheNextGlobalNineTickBoundary()
    {
        var teacher = new TeacherPolicy();
        var incoming = Playing(ballX: 0.8, ballY: 0.8, vx: 0.55, vy: 0,
            rightY: 0.5);

        var first = teacher.GetAxis(incoming);
        await Assert.That(first).IsEqualTo(1);
        await Assert.That(teacher.GetAxis(incoming with
        {
            TickNumber = 1,
            BallY = 0.2,
            RightY = 0.8
        })).IsEqualTo(first);
        await Assert.That(teacher.GetAxis(incoming with
        {
            TickNumber = 9,
            Phase = GamePhase.Countdown,
            RightY = 0.8
        })).IsEqualTo(-1);
    }

    [Test]
    public async Task GetAxis_IsDeterministicAndAlwaysLegalOnExactEngineStates()
    {
        var first = new TeacherPolicy();
        var replay = new TeacherPolicy();
        var game = new GameEngine();
        game.StartMatch();
        var observedPlayingTicks = 0;

        for (var tick = 0; tick < 210; tick++)
        {
            var state = game.CaptureCheckpoint();
            var axis = first.GetAxis(state);
            await Assert.That(axis).IsEqualTo(replay.GetAxis(state));
            await Assert.That(axis is >= -1 and <= 1).IsTrue();
            if (state.Phase == GamePhase.Playing) observedPlayingTicks++;

            game.Advance(GameConstants.FixedStepSeconds, tick % 36 < 18 ? 1 : -1, axis);
            await Assert.That(game.RightY)
                .IsBetween(GameConstants.MinPaddleY, GameConstants.MaxPaddleY);
        }

        await Assert.That(observedPlayingTicks).IsGreaterThan(0);
        first.Reset();
        var fresh = new TeacherPolicy();
        var checkpoint = game.CaptureCheckpoint();
        await Assert.That(first.GetAxis(checkpoint)).IsEqualTo(fresh.GetAxis(checkpoint));
    }

    private static GameState Playing(double ballX, double ballY, double vx, double vy,
        double rightY) => new()
    {
        Phase = GamePhase.Playing,
        LeftY = GameConstants.ArenaCenter,
        RightY = rightY,
        BallX = ballX,
        BallY = ballY,
        BallVx = vx,
        BallVy = vy,
        RecentEvents = GameEventHistory.Empty
    };
}
