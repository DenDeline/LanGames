namespace LanPong.Tests;

public sealed class RollbackTimelineTests
{
    private static readonly Guid SessionId = Guid.ParseExact("ffeeddccbbaa99887766554433221100", "N");

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task LateInput_ReplaysPaddleCollisionAndRemovesPredictedScore(PaddleSide hostSide)
    {
        var onTimeGame = NearGuestPaddle(hostSide);
        var onTime = new HostRollbackTimeline(onTimeGame);
        onTime.Reset(hostSide);
        var lateGame = NearGuestPaddle(hostSide);
        var late = new HostRollbackTimeline(lateGame);
        late.Reset(hostSide);

        for (var tick = 1; tick <= 12; tick++)
        {
            if (tick <= 4)
                onTime.Receive(Input(tick, tick, [1]));
            onTime.Advance(0);
            late.Advance(0);
        }

        await Assert.That(HostScore(onTimeGame, hostSide)).IsEqualTo(0);
        await Assert.That(hostSide == PaddleSide.Left ? onTimeGame.BallVx < 0 : onTimeGame.BallVx > 0).IsTrue();
        await Assert.That(HostScore(lateGame, hostSide)).IsEqualTo(1);
        await Assert.That(lateGame.RecentEvents.Any(item => item.Kind == GameEventKind.Goal)).IsTrue();

        var changed = late.Receive(Input(4, 4, [1, 1, 1, 1]));

        await Assert.That(changed).IsTrue();
        await Assert.That(lateGame.CaptureCheckpoint()).IsEqualTo(onTimeGame.CaptureCheckpoint());
        await Assert.That(lateGame.RecentEvents.Any(item => item.Kind == GameEventKind.Goal)).IsFalse();
        await Assert.That(lateGame.RecentEvents.Any(item => item.Kind == GameEventKind.Paddle)).IsTrue();
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task OutOfOrderRedundantPackets_ConvergeOnNewestInputForEachTick(PaddleSide hostSide)
    {
        var orderedGame = StartedMatch();
        var ordered = new HostRollbackTimeline(orderedGame);
        ordered.Reset(hostSide);
        var reversedGame = StartedMatch();
        var reversed = new HostRollbackTimeline(reversedGame);
        reversed.Reset(hostSide);
        for (var tick = 0; tick < 12; tick++)
        {
            ordered.Advance(0);
            reversed.Advance(0);
        }

        var older = Input(6, 10, [-1, -1, -1, -1, -1, -1]);
        var newer = Input(8, 20, [1, 1, 1, 1, 1, 1, 1, 1]);
        ordered.Receive(older);
        ordered.Receive(newer);
        reversed.Receive(newer);
        var staleChangedState = reversed.Receive(older);

        await Assert.That(staleChangedState).IsFalse();
        await Assert.That(reversedGame.CaptureCheckpoint()).IsEqualTo(orderedGame.CaptureCheckpoint());
        await Assert.That(GuestY(reversedGame, hostSide) > 0.5).IsTrue();
    }

    [Test]
    public async Task InputsOutsideHistoryAndFutureWindow_DoNotRewindCurrentState()
    {
        var game = StartedMatch();
        var timeline = new HostRollbackTimeline(game);
        timeline.Reset(PaddleSide.Left);
        for (var tick = 0; tick < 50; tick++) timeline.Advance(0);
        var before = game.CaptureCheckpoint();

        var oldChangedState = timeline.Receive(Input(26, 1, [1, 1, 1, 1, 1, 1, 1, 1]));
        var futureChangedState = timeline.Receive(Input(59, 2, [-1]));

        await Assert.That(oldChangedState).IsFalse();
        await Assert.That(futureChangedState).IsFalse();
        await Assert.That(game.CaptureCheckpoint()).IsEqualTo(before);
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task GuestPrediction_ReplaysLocalInputsAfterAuthoritativeCorrection(PaddleSide hostSide)
    {
        var host = StartedMatch();
        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset(hostSide);
        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: 1);

        for (var tick = 0; tick < 8; tick++) prediction.Advance(1);
        var predictedY = GuestY(guest, hostSide);
        await Assert.That(guest.TickNumber).IsEqualTo(8);
        await Assert.That(predictedY > GuestY(host, hostSide)).IsTrue();

        for (var tick = 0; tick < 4; tick++)
            host.Advance(GameConstants.FixedStepSeconds, 0, 0);
        var expected = new GameEngine();
        expected.Restore(host.Capture());
        for (var tick = 0; tick < 4; tick++)
            expected.Advance(GameConstants.FixedStepSeconds,
                hostSide == PaddleSide.Left ? 0 : 1, hostSide == PaddleSide.Left ? 1 : 0);

        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: 1);

        await Assert.That(guest.TickNumber).IsEqualTo(8);
        await Assert.That(GuestY(guest, hostSide) < predictedY).IsTrue();
        await Assert.That(guest.Capture()).IsEqualTo(expected.Capture());
        var packet = prediction.CreateInputPacket(SessionId, 99);
        await Assert.That(packet.Tick).IsEqualTo(8);
        await Assert.That(packet.Sequence).IsEqualTo(99);
        await Assert.That(packet.Axes).IsNotNull();
        await Assert.That(packet.Axes!.SequenceEqual(Enumerable.Repeat(1, 8))).IsTrue();
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task GuestPrediction_FirstSampleAfterInitialState_DoesNotBackdateInput(PaddleSide hostSide)
    {
        var host = StartedMatch();
        for (var tick = 0; tick < 10; tick++)
            host.Advance(GameConstants.FixedStepSeconds, 0, 0);

        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset(hostSide);
        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: 1);

        await Assert.That(guest.TickNumber).IsEqualTo(10);
        await Assert.That(prediction.HasCurrentInput).IsFalse();

        prediction.Advance(1);
        await Assert.That(prediction.HasCurrentInput).IsTrue();
        var packet = prediction.CreateInputPacket(SessionId, 1);
        await Assert.That(packet.Tick).IsEqualTo(11);
        await Assert.That(packet.Axes).IsNotNull();
        await Assert.That(packet.Axes!.SequenceEqual([1])).IsTrue();
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task GuestPrediction_WhenTwentyTicksAhead_RebasesInputsInsideHostAcceptanceWindow(PaddleSide hostSide)
    {
        var host = StartedMatch();
        var hostTimeline = new HostRollbackTimeline(host);
        hostTimeline.Reset(hostSide);
        for (var tick = 0; tick < 5; tick++) hostTimeline.Advance(0);
        var authoritative = host.Capture();

        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset(hostSide);
        prediction.Reconcile(authoritative, hostAxis: 0, pingMs: null, localAxis: 1);
        for (var tick = 0; tick < 20; tick++) prediction.Advance(1);
        await Assert.That(guest.TickNumber - authoritative.TickNumber).IsEqualTo(20);

        const double pingMs = 100;
        var leadTicks = (int)Math.Ceiling(pingMs / (2 * 1000 * GameConstants.FixedStepSeconds));
        prediction.Reconcile(authoritative, hostAxis: 0, pingMs: pingMs, localAxis: 1);
        await Assert.That(guest.TickNumber).IsLessThanOrEqualTo(authoritative.TickNumber + leadTicks + 4);

        // The next local tick is still within the host's eight-tick future
        // window. All of its redundant inputs are future ticks on the host.
        prediction.Advance(1);
        var packet = prediction.CreateInputPacket(SessionId, 1);
        await Assert.That(packet.Tick - host.TickNumber).IsGreaterThan(0);
        await Assert.That(packet.Tick - host.TickNumber).IsLessThanOrEqualTo(NetworkConstants.MaximumFutureInputTicks);
        await Assert.That(packet.Axes).IsNotNull();
        await Assert.That(packet.Axes!.All(axis => axis == 1)).IsTrue();
        hostTimeline.Receive(packet);
        for (var tick = host.TickNumber; tick < packet.Tick; tick++) hostTimeline.Advance(0);

        await Assert.That(host.TickNumber).IsEqualTo(packet.Tick);
        await Assert.That(GuestY(host, hostSide) > 0.6).IsTrue();
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task GuestPrediction_KeyReleaseAfterRebase_ReplacesOldSpeculativeAxis(PaddleSide hostSide)
    {
        var host = StartedMatch();
        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset(hostSide);
        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: 1);
        for (var tick = 0; tick < 20; tick++) prediction.Advance(1);
        await Assert.That(guest.TickNumber).IsEqualTo(20);

        for (var tick = 0; tick < 5; tick++)
            host.Advance(GameConstants.FixedStepSeconds, 0, 0);
        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: 100, localAxis: 0);
        await Assert.That(guest.TickNumber).IsLessThanOrEqualTo(12);

        prediction.Advance(0);
        var packet = prediction.CreateInputPacket(SessionId, 1);
        await Assert.That(packet.Tick).IsEqualTo(guest.TickNumber);
        await Assert.That(packet.Axes).IsNotNull();
        await Assert.That(packet.Axes![0]).IsEqualTo(0);
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task HostAndGuest_DelayedGuestInputConvergesAcrossPaddleCollision(PaddleSide hostSide)
    {
        var host = NearGuestPaddle(hostSide);
        var authority = new HostRollbackTimeline(host);
        authority.Reset(hostSide);
        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset(hostSide);
        prediction.Reconcile(host.Capture(), hostAxis: -1, pingMs: null, localAxis: 1);

        InputPacket? delayed = null;
        for (var tick = 1; tick <= 12; tick++)
        {
            authority.Advance(-1);
            prediction.Advance(1);
            if (tick == 4) delayed = prediction.CreateInputPacket(SessionId, 1);
        }

        await Assert.That(HostScore(host, hostSide)).IsEqualTo(1);
        await Assert.That(HostScore(guest, hostSide)).IsEqualTo(0);
        await Assert.That(authority.Receive(delayed!)).IsTrue();
        await Assert.That(host.CaptureCheckpoint()).IsEqualTo(guest.CaptureCheckpoint());

        prediction.Reconcile(host.Capture(), hostAxis: -1, pingMs: null, localAxis: 1);
        await Assert.That(guest.CaptureCheckpoint()).IsEqualTo(host.CaptureCheckpoint());
        await Assert.That(GuestY(host, hostSide) > 0.5).IsTrue();
        await Assert.That(hostSide == PaddleSide.Left ? host.LeftY < 0.5 : host.RightY < 0.5).IsTrue();
        await Assert.That(host.RecentEvents.Any(item => item.Kind == GameEventKind.Paddle)).IsTrue();
        await Assert.That(host.RecentEvents.Any(item => item.Kind == GameEventKind.Goal)).IsFalse();
    }

    [Test]
    public async Task Timelines_RequireAnExplicitResolvedHostSideBeforeUse()
    {
        var host = StartedMatch();
        var authority = new HostRollbackTimeline(host);
        var prediction = new GuestPredictionTimeline(new GameEngine());

        await Assert.That(() => authority.Advance(1)).Throws<InvalidOperationException>();
        await Assert.That(() => authority.Receive(Input(1, 1, [1]))).Throws<InvalidOperationException>();
        await Assert.That(() => prediction.Advance(1)).Throws<InvalidOperationException>();
        await Assert.That(() => prediction.Reconcile(host.Capture(), 0, null, 1)).Throws<InvalidOperationException>();
        await Assert.That(() => prediction.CreateInputPacket(SessionId, 1)).Throws<InvalidOperationException>();
        await Assert.That(host.TickNumber).IsEqualTo(0);
    }

    [Test]
    [Arguments((PaddleSide)0)]
    [Arguments((PaddleSide)3)]
    [Arguments((PaddleSide)(-1))]
    public async Task Timelines_RejectNonphysicalResolvedSides(PaddleSide invalidSide)
    {
        var authority = new HostRollbackTimeline(StartedMatch());
        var prediction = new GuestPredictionTimeline(new GameEngine());

        await Assert.That(() => authority.Reset(invalidSide)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => prediction.Reset(invalidSide)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => authority.Advance(0)).Throws<InvalidOperationException>();
        await Assert.That(() => prediction.Reconcile(StartedMatch().Capture(), 0, null, 0))
            .Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task Reset_WithAnotherSideClearsRemoteAndPredictionHistory(PaddleSide initialHostSide)
    {
        var host = StartedMatch();
        var authority = new HostRollbackTimeline(host);
        authority.Reset(initialHostSide);
        authority.Receive(Input(1, 1, [1]));
        var hostSide = PaddleSides.Opposite(initialHostSide);
        authority.Reset(hostSide);
        authority.Advance(-1);

        await Assert.That(GuestY(host, hostSide)).IsEqualTo(0.5);
        await Assert.That(hostSide == PaddleSide.Left ? host.LeftY < 0.5 : host.RightY < 0.5).IsTrue();

        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset(initialHostSide);
        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: 1);
        prediction.Advance(1);
        var beforeReset = guest.CaptureCheckpoint();
        prediction.Reset(hostSide);
        await Assert.That(prediction.Started).IsFalse();
        await Assert.That(prediction.HasCurrentInput).IsFalse();
        prediction.Advance(1);
        await Assert.That(guest.CaptureCheckpoint()).IsEqualTo(beforeReset);
        await Assert.That(() => prediction.CreateInputPacket(SessionId, 1)).Throws<InvalidOperationException>();

        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: -1);
        prediction.Advance(-1);
        await Assert.That(GuestY(guest, hostSide) < GuestY(host, hostSide)).IsTrue();
        await Assert.That(prediction.CreateInputPacket(SessionId, 2).Axes!.SequenceEqual([-1])).IsTrue();
    }

    private static InputPacket Input(long tick, long sequence, int[] axes) => new()
    {
        SessionId = SessionId, RoundId = 1, Tick = tick,
        Sequence = sequence, Axes = axes
    };

    private static GameEngine StartedMatch()
    {
        var game = new GameEngine();
        game.StartMatch();
        return game;
    }

    private static double GuestY(GameEngine game, PaddleSide hostSide) =>
        hostSide == PaddleSide.Left ? game.RightY : game.LeftY;

    private static int HostScore(GameEngine game, PaddleSide hostSide) =>
        hostSide == PaddleSide.Left ? game.LeftScore : game.RightScore;

    private static GameEngine NearGuestPaddle(PaddleSide hostSide)
    {
        var game = new GameEngine();
        game.Restore(new GameState
        {
            Phase = GamePhase.Playing,
            RoundId = 1,
            LeftY = 0.5,
            RightY = 0.5,
            BallX = hostSide == PaddleSide.Left
                ? GameConstants.RightContactX - 3 * GameConstants.FixedStepSeconds * 0.55
                : GameConstants.LeftContactX + 3 * GameConstants.FixedStepSeconds * 0.55,
            BallY = 0.635,
            BallVx = hostSide == PaddleSide.Left ? 0.55 : -0.55,
            ServeDirection = 1
        });
        return game;
    }
}
