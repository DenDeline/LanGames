namespace LanPong.Tests;

public sealed class RightBotObservationV1Tests
{
    [Test]
    public async Task Schema_HasFixedFeatureOrderAndCheckedActionMapping()
    {
        await Assert.That(RightBotObservationV1.FeatureNames.Count)
            .IsEqualTo(RightBotObservationV1.FeatureCount);
        await Assert.That(RightBotObservationV1.FeatureNames.SequenceEqual(new[]
        {
            "left_y", "right_y", "ball_x", "ball_y", "ball_vx", "ball_vy",
            "left_score_normalized", "right_score_normalized", "right_contact_y", "right_contact_time"
        })).IsTrue();

        for (var classIndex = 0; classIndex < 3; classIndex++)
        {
            var axis = RightBotObservationV1.AxisFromClass(classIndex);
            await Assert.That(axis).IsEqualTo(classIndex - 1);
            await Assert.That(RightBotObservationV1.ClassFromAxis(axis)).IsEqualTo(classIndex);
        }

        var invalidClassRejected = false;
        try { RightBotObservationV1.AxisFromClass(3); }
        catch (ArgumentOutOfRangeException) { invalidClassRejected = true; }
        await Assert.That(invalidClassRejected).IsTrue();

        var invalidAxisRejected = false;
        try { RightBotObservationV1.ClassFromAxis(-2); }
        catch (ArgumentOutOfRangeException) { invalidAxisRejected = true; }
        await Assert.That(invalidAxisRejected).IsTrue();
    }

    [Test]
    public async Task Encode_UsesOnlyPresentVisibleStateAndWritesFloat32InOrder()
    {
        var state = new GameState
        {
            LeftY = 0.25, RightY = 0.75,
            BallX = 0.4, BallY = 0.6, BallVx = 0.5, BallVy = -0.2,
            LeftScore = 3, RightScore = 4,
            Phase = GamePhase.Playing,
            TickNumber = 51, RoundId = 2, ServeDirection = -1, Hits = 4,
            LastEventTick = 50, EventOrdinal = 3, RecentEvents = GameEventHistory.Empty
        };
        var time = (GameConstants.RightContactX - state.BallX) / state.BallVx;
        float[] expected =
        [
            0.25f, 0.75f, 0.4f, 0.6f, 0.5f, -0.2f,
            3f / GameConstants.WinningScore, 4f / GameConstants.WinningScore,
            (float)(state.BallY + state.BallVy * time), (float)time
        ];

        var encoded = RightBotObservationV1.Encode(state);
        await Assert.That(encoded.SequenceEqual(expected)).IsTrue();

        var buffer = Enumerable.Repeat(-99f, RightBotObservationV1.FeatureCount + 2).ToArray();
        RightBotObservationV1.Encode(state, buffer);
        await Assert.That(buffer.AsSpan(0, RightBotObservationV1.FeatureCount).SequenceEqual(expected)).IsTrue();
        await Assert.That(buffer[^1]).IsEqualTo(-99f);

        var alteredHiddenState = state with
        {
            TickNumber = 9000, RoundId = 20, ServeDirection = 1, Hits = 99,
            LastEventTick = 9000, EventOrdinal = 11
        };
        await Assert.That(RightBotObservationV1.Encode(alteredHiddenState).SequenceEqual(encoded)).IsTrue();
    }

    [Test]
    public async Task Encode_ReflectsBothWallsAndCapsLongContactTime()
    {
        var state = new GameState
        {
            BallX = GameConstants.RightContactX - 0.5,
            BallY = 0.1,
            BallVx = 0.5,
            BallVy = -0.3
        };
        var top = RightBotObservationV1.Encode(state);
        await Assert.That(top[8]).IsEqualTo(0.224f).Within(1e-6f);
        await Assert.That(top[9]).IsEqualTo(1f);

        var bottom = RightBotObservationV1.Encode(state with { BallY = 0.9, BallVy = 0.3 });
        await Assert.That(bottom[8]).IsEqualTo(0.776f).Within(1e-6f);
        await Assert.That(bottom[9]).IsEqualTo(1f);

        var longFlight = RightBotObservationV1.Encode(state with
        {
            BallX = 0,
            BallY = 0.5,
            BallVx = 0.1,
            BallVy = 0
        });
        await Assert.That(longFlight[8]).IsEqualTo(0.5f);
        await Assert.That(longFlight[9]).IsEqualTo(2f);
    }

    [Test]
    public async Task Encode_WhenBallDoesNotApproachRight_UsesFiniteSentinelsAndRejectsBadBuffers()
    {
        var game = new GameEngine();
        game.StartMatch();
        var countdown = RightBotObservationV1.Encode(game.Capture());
        await Assert.That(countdown[8]).IsEqualTo(0.5f);
        await Assert.That(countdown[9]).IsEqualTo(2f);
        await Assert.That(countdown.All(float.IsFinite)).IsTrue();

        var away = RightBotObservationV1.Encode(new GameState
        {
            BallX = 0.5, BallY = 0.7, BallVx = -0.5, BallVy = 0.1
        });
        await Assert.That(away[8]).IsEqualTo(0.5f);
        await Assert.That(away[9]).IsEqualTo(2f);
        await Assert.That(away.All(float.IsFinite)).IsTrue();

        var shortBufferRejected = false;
        try { RightBotObservationV1.Encode(game.Capture(), new float[RightBotObservationV1.FeatureCount - 1]); }
        catch (ArgumentException) { shortBufferRejected = true; }
        await Assert.That(shortBufferRejected).IsTrue();

        var nonFiniteStateRejected = false;
        try { RightBotObservationV1.Encode(new GameState { BallY = double.NaN }); }
        catch (ArgumentOutOfRangeException) { nonFiniteStateRejected = true; }
        await Assert.That(nonFiniteStateRejected).IsTrue();
    }
}
