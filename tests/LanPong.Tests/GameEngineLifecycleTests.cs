namespace LanPong.Tests;

public sealed class GameEngineLifecycleTests
{
    private const double BallRadiusX = 0.012 * 9.0 / 16.0;

    [Test]
    public async Task Advance_WhenCountdownExpiresInsideStep_UsesRemainingTimeForServe()
    {
        var game = new GameEngine();
        game.StartMatch();

        game.Advance(1.625, 0, 0);

        await Assert.That(game.Phase).IsEqualTo(GamePhase.Playing);
        await Assert.That(game.Countdown).IsEqualTo(0);
        await Assert.That(game.BallX).IsEqualTo(0.5 + 0.55 * 0.025).Within(1e-10);
        await Assert.That(game.BallY).IsEqualTo(0.5 + 0.19 * 0.025).Within(1e-10);
        await Assert.That(game.BallVx).IsEqualTo(0.55);
        await Assert.That(game.BallVy).IsEqualTo(0.19);
    }

    [Test]
    public async Task Advance_WhenCountdownExpiresAtStepBoundary_StartsPlayingWithoutBallMovement()
    {
        var game = new GameEngine();
        game.StartMatch();

        game.Advance(1.6, 0, 0);

        await Assert.That(game.Phase).IsEqualTo(GamePhase.Playing);
        await Assert.That(game.Countdown).IsEqualTo(0);
        await Assert.That(game.BallX).IsEqualTo(0.5);
        await Assert.That(game.BallY).IsEqualTo(0.5);
        await Assert.That(game.BallVx).IsEqualTo(0.55);
        await Assert.That(game.BallVy).IsEqualTo(0.19);
    }

    [Test]
    public async Task Advance_WhenBallCrossesGoalInsideStep_AppliesRemainingTimeToNextCountdown()
    {
        var game = Playing(0.01, 0.9, -1, 0);
        const double step = 0.05;
        var timeToGoal = (0.01 + BallRadiusX) / 1.0;

        game.Advance(step, 0, 0);

        await Assert.That(game.RightScore).IsEqualTo(1);
        await Assert.That(game.LeftScore).IsEqualTo(0);
        await Assert.That(game.Phase).IsEqualTo(GamePhase.Countdown);
        await Assert.That(game.Countdown).IsEqualTo(1.6 - (step - timeToGoal)).Within(1e-10);
        await Assert.That(game.BallX).IsEqualTo(0.5);
        await Assert.That(game.BallY).IsEqualTo(0.5);
    }

    [Test]
    public async Task Advance_WhenSeventhPointIsScored_EndsMatchAndRestartClearsScore()
    {
        var game = Playing(0.99, 0.9, 1, 0, leftScore: 6, roundId: 12, sequence: 99);

        game.Advance(0.05, 0, 0);

        await Assert.That(game.LeftScore).IsEqualTo(7);
        await Assert.That(game.RightScore).IsEqualTo(0);
        await Assert.That(game.Phase).IsEqualTo(GamePhase.GameOver);
        await Assert.That(game.Countdown).IsEqualTo(0);
        await Assert.That(game.BallVx).IsEqualTo(0);
        await Assert.That(game.BallVy).IsEqualTo(0);
        await Assert.That(game.RoundId).IsEqualTo(12);
        await Assert.That(game.TickNumber).IsEqualTo(100);

        var finishedX = game.BallX;
        game.Advance(1.0 / 60, 1, -1);
        await Assert.That(game.TickNumber).IsEqualTo(101);
        await Assert.That(game.BallX).IsEqualTo(finishedX);

        game.StartMatch();
        await Assert.That(game.Phase).IsEqualTo(GamePhase.Countdown);
        await Assert.That(game.RoundId).IsEqualTo(13);
        await Assert.That(game.LeftScore).IsEqualTo(0);
        await Assert.That(game.RightScore).IsEqualTo(0);
        await Assert.That(game.BallX).IsEqualTo(0.5);
        await Assert.That(game.BallY).IsEqualTo(0.5);
    }

    [Test]
    public async Task Advance_WhenWinningGoalOccursInsideStep_StopsPaddlesAtGoalTime()
    {
        var game = Playing(0.99, 0.9, 1, 0, leftScore: 6);
        var timeToGoal = 1 + BallRadiusX - 0.99;

        game.Advance(0.05, 1, -1);

        await Assert.That(game.Phase).IsEqualTo(GamePhase.GameOver);
        await Assert.That(game.LeftY).IsEqualTo(0.5 + 0.85 * timeToGoal).Within(1e-10);
        await Assert.That(game.RightY).IsEqualTo(0.5 - 0.85 * timeToGoal).Within(1e-10);
    }

    [Test]
    public async Task Advance_WhenInputSequenceIsReplayed_ProducesIdenticalState()
    {
        var first = new GameEngine();
        var replay = new GameEngine();
        first.StartMatch();
        replay.StartMatch();

        for (var tick = 0; tick < 600; tick++)
        {
            var leftAxis = tick % 90 < 30 ? -1 : tick % 90 < 60 ? 1 : 0;
            var rightAxis = tick % 72 < 24 ? 1 : tick % 72 < 48 ? -1 : 0;
            first.Advance(1.0 / 60, leftAxis, rightAxis);
            replay.Advance(1.0 / 60, leftAxis, rightAxis);
        }

        await Assert.That(replay.LeftY).IsEqualTo(first.LeftY);
        await Assert.That(replay.RightY).IsEqualTo(first.RightY);
        await Assert.That(replay.BallX).IsEqualTo(first.BallX);
        await Assert.That(replay.BallY).IsEqualTo(first.BallY);
        await Assert.That(replay.BallVx).IsEqualTo(first.BallVx);
        await Assert.That(replay.BallVy).IsEqualTo(first.BallVy);
        await Assert.That(replay.LeftScore).IsEqualTo(first.LeftScore);
        await Assert.That(replay.RightScore).IsEqualTo(first.RightScore);
        await Assert.That(replay.Phase).IsEqualTo(first.Phase);
        await Assert.That(replay.Countdown).IsEqualTo(first.Countdown);
        await Assert.That(replay.TickNumber).IsEqualTo(600);
        await Assert.That(replay.RoundId).IsEqualTo(first.RoundId);
    }

    private static GameEngine Playing(
        double ballX, double ballY, double vx, double vy,
        int leftScore = 0, int rightScore = 0, int roundId = 0, long sequence = 0)
    {
        var game = new GameEngine();
        game.Restore(new GameState
        {
            Phase = GamePhase.Playing,
            BallX = ballX,
            BallY = ballY,
            BallVx = vx,
            BallVy = vy,
            LeftY = 0.5,
            RightY = 0.5,
            LeftScore = leftScore,
            RightScore = rightScore,
            RoundId = roundId,
            TickNumber = sequence
        });
        return game;
    }
}
