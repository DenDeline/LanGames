namespace LanPong.Tests;

public sealed class RollbackTimelineTests
{
    [Test]
    public async Task LateInput_ReplaysPaddleCollisionAndRemovesPredictedScore()
    {
        var onTimeGame = NearRightPaddle();
        var onTime = new HostRollbackTimeline(onTimeGame);
        onTime.Reset();
        var lateGame = NearRightPaddle();
        var late = new HostRollbackTimeline(lateGame);
        late.Reset();

        for (var tick = 1; tick <= 12; tick++)
        {
            if (tick <= 4)
                onTime.Receive(Input(tick, tick, [1]));
            onTime.Advance(0);
            late.Advance(0);
        }

        await Assert.That(onTimeGame.LeftScore).IsEqualTo(0);
        await Assert.That(onTimeGame.BallVx < 0).IsTrue();
        await Assert.That(lateGame.LeftScore).IsEqualTo(1);

        var changed = late.Receive(Input(4, 4, [1, 1, 1, 1]));

        await Assert.That(changed).IsTrue();
        await Assert.That(lateGame.CaptureCheckpoint()).IsEqualTo(onTimeGame.CaptureCheckpoint());
    }

    [Test]
    public async Task OutOfOrderRedundantPackets_ConvergeOnNewestInputForEachTick()
    {
        var orderedGame = StartedMatch();
        var ordered = new HostRollbackTimeline(orderedGame);
        ordered.Reset();
        var reversedGame = StartedMatch();
        var reversed = new HostRollbackTimeline(reversedGame);
        reversed.Reset();
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
        await Assert.That(reversedGame.RightY > 0.5).IsTrue();
    }

    [Test]
    public async Task InputsOutsideHistoryAndFutureWindow_DoNotRewindCurrentState()
    {
        var game = StartedMatch();
        var timeline = new HostRollbackTimeline(game);
        timeline.Reset();
        for (var tick = 0; tick < 50; tick++) timeline.Advance(0);
        var before = game.CaptureCheckpoint();

        var oldChangedState = timeline.Receive(Input(26, 1, [1, 1, 1, 1, 1, 1, 1, 1]));
        var futureChangedState = timeline.Receive(Input(59, 2, [-1]));

        await Assert.That(oldChangedState).IsFalse();
        await Assert.That(futureChangedState).IsFalse();
        await Assert.That(game.CaptureCheckpoint()).IsEqualTo(before);
    }

    [Test]
    public async Task GuestPrediction_ReplaysLocalInputsAfterAuthoritativeCorrection()
    {
        var host = StartedMatch();
        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset();
        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: 1);

        for (var tick = 0; tick < 8; tick++) prediction.Advance(1);
        var predictedY = guest.RightY;
        await Assert.That(guest.TickNumber).IsEqualTo(8);
        await Assert.That(predictedY > host.RightY).IsTrue();

        for (var tick = 0; tick < 4; tick++)
            host.Advance(GameConstants.FixedStepSeconds, 0, 0);
        var expected = new GameEngine();
        expected.Restore(host.Capture());
        for (var tick = 0; tick < 4; tick++)
            expected.Advance(GameConstants.FixedStepSeconds, 0, 1);

        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: 1);

        await Assert.That(guest.TickNumber).IsEqualTo(8);
        await Assert.That(guest.RightY < predictedY).IsTrue();
        await Assert.That(guest.Capture()).IsEqualTo(expected.Capture());
        var packet = prediction.CreateInputPacket("session", 99);
        await Assert.That(packet.Tick).IsEqualTo(8);
        await Assert.That(packet.Sequence).IsEqualTo(99);
        await Assert.That(packet.Axes).IsNotNull();
        await Assert.That(packet.Axes!.SequenceEqual(Enumerable.Repeat(1, 8))).IsTrue();
    }

    [Test]
    public async Task GuestPrediction_FirstSampleAfterInitialState_DoesNotBackdateInput()
    {
        var host = StartedMatch();
        for (var tick = 0; tick < 10; tick++)
            host.Advance(GameConstants.FixedStepSeconds, 0, 0);

        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset();
        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: 1);

        await Assert.That(guest.TickNumber).IsEqualTo(10);
        await Assert.That(prediction.HasCurrentInput).IsFalse();

        prediction.Advance(1);
        await Assert.That(prediction.HasCurrentInput).IsTrue();
        var packet = prediction.CreateInputPacket("session", 1);
        await Assert.That(packet.Tick).IsEqualTo(11);
        await Assert.That(packet.Axes).IsNotNull();
        await Assert.That(packet.Axes!.SequenceEqual([1])).IsTrue();
    }

    [Test]
    public async Task GuestPrediction_WhenTwentyTicksAhead_RebasesInputsInsideHostAcceptanceWindow()
    {
        var host = StartedMatch();
        var hostTimeline = new HostRollbackTimeline(host);
        hostTimeline.Reset();
        for (var tick = 0; tick < 5; tick++) hostTimeline.Advance(0);
        var authoritative = host.Capture();

        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset();
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
        var packet = prediction.CreateInputPacket("session", 1);
        await Assert.That(packet.Tick - host.TickNumber).IsGreaterThan(0);
        await Assert.That(packet.Tick - host.TickNumber).IsLessThanOrEqualTo(NetworkConstants.MaximumFutureInputTicks);
        await Assert.That(packet.Axes).IsNotNull();
        await Assert.That(packet.Axes!.All(axis => axis == 1)).IsTrue();
        hostTimeline.Receive(packet);
        for (var tick = host.TickNumber; tick < packet.Tick; tick++) hostTimeline.Advance(0);

        await Assert.That(host.TickNumber).IsEqualTo(packet.Tick);
        await Assert.That(host.RightY > 0.6).IsTrue();
    }

    [Test]
    public async Task GuestPrediction_KeyReleaseAfterRebase_ReplacesOldSpeculativeAxis()
    {
        var host = StartedMatch();
        var guest = new GameEngine();
        var prediction = new GuestPredictionTimeline(guest);
        prediction.Reset();
        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: null, localAxis: 1);
        for (var tick = 0; tick < 20; tick++) prediction.Advance(1);
        await Assert.That(guest.TickNumber).IsEqualTo(20);

        for (var tick = 0; tick < 5; tick++)
            host.Advance(GameConstants.FixedStepSeconds, 0, 0);
        prediction.Reconcile(host.Capture(), hostAxis: 0, pingMs: 100, localAxis: 0);
        await Assert.That(guest.TickNumber).IsLessThanOrEqualTo(12);

        prediction.Advance(0);
        var packet = prediction.CreateInputPacket("session", 1);
        await Assert.That(packet.Tick).IsEqualTo(guest.TickNumber);
        await Assert.That(packet.Axes).IsNotNull();
        await Assert.That(packet.Axes![0]).IsEqualTo(0);
    }

    private static InputPacket Input(long tick, long sequence, int[] axes) => new()
    {
        SessionId = "session", RoundId = 1, Tick = tick,
        Sequence = sequence, Axes = axes
    };

    private static GameEngine StartedMatch()
    {
        var game = new GameEngine();
        game.StartMatch();
        return game;
    }

    private static GameEngine NearRightPaddle()
    {
        var game = new GameEngine();
        game.Restore(new GameState
        {
            Phase = GamePhase.Playing,
            RoundId = 1,
            LeftY = 0.5,
            RightY = 0.5,
            BallX = GameConstants.RightContactX - 3 * GameConstants.FixedStepSeconds * 0.55,
            BallY = 0.635,
            BallVx = 0.55,
            ServeDirection = 1
        });
        return game;
    }
}
