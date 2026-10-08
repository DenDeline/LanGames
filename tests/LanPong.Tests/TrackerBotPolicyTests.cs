namespace LanPong.Tests;

public sealed class TrackerBotPolicyTests
{
    [Test]
    public async Task GetAxis_TracksApproachAndCentersWhenBallRetreats()
    {
        var bot = new TrackerBotPolicy();

        await Assert.That(bot.GetAxis(Playing(rightY: 0.5, ballY: 0.75, vx: 0.55))).IsEqualTo(1);
        bot.Reset();
        await Assert.That(bot.GetAxis(Playing(rightY: 0.8, ballY: 0.9, vx: -0.55))).IsEqualTo(-1);
        bot.Reset();
        await Assert.That(bot.GetAxis(Playing(rightY: 0.5, ballY: 0.9, vx: -0.55))).IsEqualTo(0);
    }

    [Test]
    public async Task GetAxis_WaitsForLateApproachAndNextSampleBeforeTracking()
    {
        var bot = new TrackerBotPolicy();
        var beforeActivation = Playing(rightY: 0.5, ballY: 0.82, vx: 0.55,
            ballX: TrackerBotPolicy.ObservationActivationX - 0.001, tick: 100);

        await Assert.That(bot.GetAxis(beforeActivation)).IsEqualTo(0);
        await Assert.That(bot.GetAxis(beforeActivation with
        {
            BallX = TrackerBotPolicy.ObservationActivationX + 0.001,
            TickNumber = 101
        })).IsEqualTo(0);
        await Assert.That(bot.GetAxis(beforeActivation with
        {
            BallX = TrackerBotPolicy.ObservationActivationX + 0.001,
            TickNumber = 100 + TrackerBotPolicy.ObservationIntervalTicks
        })).IsEqualTo(1);
    }

    [Test]
    public async Task GetAxis_ReflectsShortLookaheadAtBothWalls()
    {
        var bot = new TrackerBotPolicy();

        // A downward ball will bounce before the next observation. Following its
        // current position would move toward the bottom instead of back up.
        await Assert.That(bot.GetAxis(Playing(rightY: 0.91, ballY: 0.98, vx: 0.55, vy: 0.5)))
            .IsEqualTo(-1);
        bot.Reset();
        await Assert.That(bot.GetAxis(Playing(rightY: 0.09, ballY: 0.02, vx: 0.55, vy: -0.5)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task GetAxis_LimitsObservationRateAndResetsForServeAndRematch()
    {
        var bot = new TrackerBotPolicy();
        await Assert.That(bot.GetAxis(Playing(rightY: 0.5, ballY: 0.8, vx: 0.55, tick: 100)))
            .IsEqualTo(1);

        for (var tick = 101; tick < 100 + TrackerBotPolicy.ObservationIntervalTicks; tick++)
            await Assert.That(bot.GetAxis(Playing(rightY: 0.5, ballY: 0.2, vx: 0.55, tick: tick)))
                .IsEqualTo(1);
        await Assert.That(bot.GetAxis(Playing(rightY: 0.5, ballY: 0.2, vx: 0.55,
            tick: 100 + TrackerBotPolicy.ObservationIntervalTicks))).IsEqualTo(-1);

        await Assert.That(bot.GetAxis(Playing(rightY: 0.8, phase: GamePhase.Countdown, tick: 200)))
            .IsEqualTo(-1);
        await Assert.That(bot.GetAxis(Playing(rightY: 0.5, ballY: 0.8, vx: 0.55, tick: 201)))
            .IsEqualTo(1);
        await Assert.That(bot.GetAxis(Playing(rightY: 0.8, phase: GamePhase.GameOver, tick: 202)))
            .IsEqualTo(0);

        bot.Reset();
        await Assert.That(bot.GetAxis(Playing(rightY: 0.5, ballY: 0.2, vx: 0.55, tick: 500)))
            .IsEqualTo(-1);
    }

    [Test]
    public async Task GetAxis_UsesLegalInputsAndNeverMovesPaddleBeyondBounds()
    {
        var bot = new TrackerBotPolicy();
        await Assert.That(bot.GetAxis(Playing(rightY: GameConstants.MaxPaddleY,
            ballY: GameConstants.BottomContactY, vx: 0.55))).IsEqualTo(0);
        bot.Reset();
        await Assert.That(bot.GetAxis(Playing(rightY: GameConstants.MinPaddleY,
            ballY: GameConstants.TopContactY, vx: 0.55))).IsEqualTo(0);

        var game = new GameEngine();
        game.StartMatch();
        for (var tick = 0; tick < 500; tick++)
        {
            var before = game.RightY;
            var axis = bot.GetAxis(game.Capture());
            await Assert.That(axis is >= -1 and <= 1).IsTrue();
            game.Advance(GameConstants.FixedStepSeconds, 0, axis);
            await Assert.That(game.RightY).IsBetween(GameConstants.MinPaddleY, GameConstants.MaxPaddleY);
            await Assert.That(Math.Abs(game.RightY - before))
                .IsLessThanOrEqualTo(GameConstants.PaddleSpeed * GameConstants.FixedStepSeconds + 1e-12);
        }
    }

    [Test]
    public async Task GetAxis_WhenBallCannotBeReached_EngineScoresNormalGoal()
    {
        var game = new GameEngine();
        game.Restore(Playing(rightY: 0.1, ballX: 0.91, ballY: 0.9, vx: 0.55));
        var bot = new TrackerBotPolicy();

        for (var tick = 0; tick < 20; tick++)
            game.Advance(GameConstants.FixedStepSeconds, 0, bot.GetAxis(game.Capture()));

        await Assert.That(game.LeftScore).IsEqualTo(1);
        await Assert.That(game.RightScore).IsEqualTo(0);
        await Assert.That(game.Phase).IsEqualTo(GamePhase.Countdown);
        await Assert.That(game.RecentEvents.Any(gameEvent => gameEvent.Kind == GameEventKind.Goal)).IsTrue();
    }

    [Test]
    public async Task GetAxis_ProducesDeterministicFullMatchesWithServesRalliesAndRematch()
    {
        var totalRallies = 0;
        foreach (var seed in new[] { 3, 17, 41 })
        {
            var first = RunMatch(seed);
            var replay = RunMatch(seed);

            await Assert.That(first.State).IsEqualTo(replay.State);
            await Assert.That(first.Serves).IsEqualTo(replay.Serves);
            await Assert.That(first.RightPaddleHits).IsEqualTo(replay.RightPaddleHits);
            await Assert.That(first.Rallies).IsEqualTo(replay.Rallies);
            await Assert.That(first.Goals).IsEqualTo(replay.Goals);
            await Assert.That(first.State.Phase).IsEqualTo(GamePhase.GameOver);
            await Assert.That(Math.Max(first.State.LeftScore, first.State.RightScore))
                .IsEqualTo(GameConstants.WinningScore);
            await Assert.That(first.Serves).IsGreaterThan(0);
            await Assert.That(first.RightPaddleHits).IsGreaterThan(0);
            await Assert.That(first.Goals).IsGreaterThanOrEqualTo(GameConstants.WinningScore);
            await Assert.That(first.State.RightScore).IsGreaterThan(0);
            totalRallies += first.Rallies;
        }
        await Assert.That(totalRallies).IsGreaterThan(0);

        var game = new GameEngine();
        var bot = new TrackerBotPolicy();
        game.StartMatch();
        AdvanceUntilGameOver(game, bot, 3);
        var previousRound = game.RoundId;
        bot.Reset();
        game.StartMatch();
        await Assert.That(game.RoundId).IsEqualTo(previousRound + 1);
        await Assert.That(game.Phase).IsEqualTo(GamePhase.Countdown);
        await Assert.That(game.LeftScore).IsEqualTo(0);
        await Assert.That(game.RightScore).IsEqualTo(0);
        await Assert.That(bot.GetAxis(game.Capture())).IsEqualTo(0);
    }

    private static MatchResult RunMatch(int seed)
    {
        var game = new GameEngine();
        var bot = new TrackerBotPolicy();
        game.StartMatch();
        return AdvanceUntilGameOver(game, bot, seed);
    }

    private static MatchResult AdvanceUntilGameOver(GameEngine game, TrackerBotPolicy bot, int seed)
    {
        var seen = new HashSet<string>();
        var serves = 0;
        var rightPaddleHits = 0;
        var rallies = 0;
        var goals = 0;
        var hitsThisPoint = 0;
        var random = new Random(seed);
        var leftAxis = 0;
        for (var tick = 0; tick < 12_000 && game.Phase != GamePhase.GameOver; tick++)
        {
            if (tick % 24 == 0) leftAxis = random.Next(-1, 2);
            game.Advance(GameConstants.FixedStepSeconds, leftAxis, bot.GetAxis(game.Capture()));
            foreach (var gameEvent in game.RecentEvents)
            {
                if (!seen.Add(gameEvent.Id)) continue;
                if (gameEvent.Kind == GameEventKind.Serve)
                {
                    serves++;
                    hitsThisPoint = 0;
                }
                if (gameEvent.Kind == GameEventKind.Paddle)
                {
                    hitsThisPoint++;
                    if (Math.Abs(gameEvent.X - GameConstants.RightContactX) < 1e-10)
                        rightPaddleHits++;
                }
                if (gameEvent.Kind == GameEventKind.Goal)
                {
                    goals++;
                    if (hitsThisPoint >= 2) rallies++;
                    hitsThisPoint = 0;
                }
            }
        }
        return new MatchResult(game.Capture(), serves, rightPaddleHits, rallies, goals);
    }

    private static GameState Playing(double rightY, double ballY = 0.5, double vx = 0,
        double vy = 0, long tick = 0, double ballX = 0.82, GamePhase phase = GamePhase.Playing) => new()
    {
        Phase = phase, RightY = rightY, LeftY = GameConstants.ArenaCenter,
        BallX = ballX, BallY = ballY, BallVx = vx, BallVy = vy,
        TickNumber = tick, RecentEvents = GameEventHistory.Empty
    };

    private readonly record struct MatchResult(GameState State, int Serves, int RightPaddleHits,
        int Rallies, int Goals);
}
