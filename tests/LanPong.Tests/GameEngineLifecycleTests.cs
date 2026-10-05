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
    public async Task GameEvents_DescribeMatchServeContactsAndGoalWithDistinctReplayIds()
    {
        var match = new GameEngine();
        match.StartMatch();
        await Assert.That(match.RecentEvents.Single().Kind).IsEqualTo(GameEventKind.MatchStart);
        await Assert.That(match.RecentEvents.Single().Id).IsEqualTo("0:0:5");

        match.Advance(1.6, 0, 0);
        await Assert.That(match.RecentEvents[^1].Kind).IsEqualTo(GameEventKind.Serve);
        await Assert.That(match.RecentEvents[^1].Id).IsEqualTo("1:0:1");

        var paddle = Playing(GameConstants.LeftContactX + 0.005, 0.5, -1, 0);
        paddle.Advance(0.01, 0, 0);
        var paddleEvent = paddle.RecentEvents.Single();
        await Assert.That(paddleEvent.Kind).IsEqualTo(GameEventKind.Paddle);
        await Assert.That(paddleEvent.Id).IsEqualTo("1:0:2");

        var wall = Playing(0.5, GameConstants.TopContactY + 0.005, 0.1, -1);
        wall.Advance(0.01, 0, 0);
        var wallEvent = wall.RecentEvents.Single();
        await Assert.That(wallEvent.Kind).IsEqualTo(GameEventKind.Wall);
        await Assert.That(wallEvent.Id).IsEqualTo("1:0:3");

        var goal = Playing(0.001, 0.9, -1, 0);
        goal.Advance(0.02, 0, 0);
        var goalEvent = goal.RecentEvents.Single();
        await Assert.That(goalEvent.Kind).IsEqualTo(GameEventKind.Goal);
        await Assert.That(goalEvent.Id).IsEqualTo("1:0:4");
        await Assert.That(goalEvent.X).IsEqualTo(0);
        await Assert.That(goal.RightScore).IsEqualTo(1);

        await Assert.That(new[] { paddleEvent.Id, wallEvent.Id, goalEvent.Id }.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task RestoreCheckpoint_ReplaysEventIdsAndRecentHistory()
    {
        var game = Playing(0.5, GameConstants.TopContactY + 0.005, 0.1, -1);
        var checkpoint = game.CaptureCheckpoint();
        game.Advance(0.01, 0, 0);

        var replay = new GameEngine();
        replay.RestoreCheckpoint(checkpoint);
        replay.Advance(0.01, 0, 0);

        await Assert.That(replay.RecentEvents.SequenceEqual(game.RecentEvents)).IsTrue();
        await Assert.That(replay.CaptureCheckpoint()).IsEqualTo(game.CaptureCheckpoint());
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

    [Test]
    public async Task RestoreCheckpoint_AfterPaddleBounce_ReplaysNextBounceAtSameSpeed()
    {
        var first = Playing(0.045 + 0.009 + BallRadiusX + 0.55 * 0.01, 0.5, -0.55, 0);
        first.Advance(1.0 / 60, 0, 0);
        var checkpoint = first.CaptureCheckpoint();
        await Assert.That(checkpoint.Hits).IsEqualTo(1);

        var replay = new GameEngine();
        replay.RestoreCheckpoint(checkpoint);
        var networkReplica = new GameEngine();
        networkReplica.Restore(checkpoint);

        for (var tick = 0; tick < 100; tick++)
        {
            first.Advance(1.0 / 60, 0, 0);
            replay.Advance(1.0 / 60, 0, 0);
            networkReplica.Advance(1.0 / 60, 0, 0);
        }

        await Assert.That(first.BallVx).IsEqualTo(-0.62).Within(1e-10);
        await Assert.That(first.CaptureCheckpoint()).IsEqualTo(replay.CaptureCheckpoint());
        await Assert.That(first.Capture()).IsEqualTo(networkReplica.Capture());
    }

    [Test]
    public async Task RestoreCheckpoint_AfterRightPlayerScores_ReplaysServeTowardLeft()
    {
        var first = Playing(0.01, 0.9, -1, 0);
        first.Advance(0.02, 0, 0);
        var checkpoint = first.CaptureCheckpoint();
        await Assert.That(checkpoint.Phase).IsEqualTo(GamePhase.Countdown);
        await Assert.That(checkpoint.ServeDirection).IsEqualTo(-1);

        var replay = new GameEngine();
        replay.RestoreCheckpoint(checkpoint);
        var networkReplica = new GameEngine();
        networkReplica.Restore(checkpoint);

        first.Advance(1.6, 0, 0);
        replay.Advance(1.6, 0, 0);
        networkReplica.Advance(1.6, 0, 0);

        await Assert.That(first.BallVx < 0).IsTrue();
        await Assert.That(first.CaptureCheckpoint()).IsEqualTo(replay.CaptureCheckpoint());
        await Assert.That(first.Capture()).IsEqualTo(networkReplica.Capture());
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
