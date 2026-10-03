using LanPong;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LanPong.Tests;

public sealed class GameEngineTests
{
    private const double RadiusY = 0.012;
    private const double RadiusX = RadiusY * 9.0 / 16.0;
    private const double LeftContactX = 0.045 + 0.009 + RadiusX;
    private const double RightContactX = 0.955 - 0.009 - RadiusX;

    [Test]
    public async Task Advance_WhenBallHitsLeftPaddle_BouncesAndUsesRemainingFrameTime()
    {
        var game = Playing(0.065, 0.5, -0.55, 0);
        const double dt = 1.0 / 60;
        var timeToContact = (LeftContactX - 0.065) / -0.55;
        var expectedX = LeftContactX + 0.585 * (dt - timeToContact);

        game.Advance(dt, 0, 0);

        await Assert.That(game.BallVx > 0).IsTrue();
        await Assert.That(game.BallX).IsEqualTo(expectedX).Within(1e-8);
        await Assert.That(game.BallY).IsEqualTo(0.5).Within(1e-8);
    }

    [Test]
    public async Task Advance_WhenBallHitsRightPaddle_BouncesAndUsesRemainingFrameTime()
    {
        var game = Playing(0.935, 0.5, 0.55, 0);
        const double dt = 1.0 / 60;
        var timeToContact = (RightContactX - 0.935) / 0.55;
        var expectedX = RightContactX - 0.585 * (dt - timeToContact);

        game.Advance(dt, 0, 0);

        await Assert.That(game.BallVx < 0).IsTrue();
        await Assert.That(game.BallX).IsEqualTo(expectedX).Within(1e-8);
    }

    [Test]
    public async Task Advance_WhenBallHitsTopWall_ReflectsAtBallEdge()
    {
        var game = Playing(0.5, 0.015, 0, -1);

        game.Advance(0.01, 0, 0);

        await Assert.That(game.BallY).IsEqualTo(RadiusY + 0.007).Within(1e-8);
        await Assert.That(game.BallVy).IsEqualTo(1).Within(1e-8);
    }

    [Test]
    public async Task Advance_WhenMovingPaddleArrivesAfterContact_DoesNotHitBall()
    {
        // The paddle overlaps the ball by the end of the step, but arrives too late.
        var game = Playing(LeftContactX + 0.55 * 0.04, 0.65, -0.55, 0);

        game.Advance(0.1, 1, 0);

        await Assert.That(game.BallVx < 0).IsTrue();
        await Assert.That(game.LeftScore).IsEqualTo(0);
        await Assert.That(game.RightScore).IsEqualTo(0);
        await Assert.That(game.BallX).IsEqualTo(LeftContactX - 0.55 * 0.06).Within(2e-6);
    }

    [Test]
    public async Task Advance_WhenMovingPaddleIsAlignedAtContact_BouncesBall()
    {
        // The paddle is aligned at contact, then moves away by the end of the step.
        var game = Playing(LeftContactX + 0.55 * 0.02, 0.57, -0.55, 0);

        game.Advance(0.1, -1, 0);

        await Assert.That(game.BallVx > 0).IsTrue();
        await Assert.That(game.BallX > LeftContactX).IsTrue();
        await Assert.That(game.LeftY).IsEqualTo(0.5 - 0.85 * 0.1).Within(1e-8);
    }

    [Test]
    public async Task Advance_WhenMissedBallCrossesScoringEdge_AwardsPointToOpponent()
    {
        var game = Playing(0.01, 0.9, -1.5, 0);

        game.Advance(0.02, 0, 0);

        await Assert.That(game.RightScore).IsEqualTo(1);
        await Assert.That(game.LeftScore).IsEqualTo(0);
        await Assert.That(game.Phase).IsEqualTo(GamePhase.Countdown);
        await Assert.That(game.BallX).IsEqualTo(0.5).Within(1e-8);
    }

    [Test]
    public async Task Advance_WhenBallMissesAtPaddlePlaneOnFrameBoundary_DoesNotBounceOnNextFrame()
    {
        var game = Playing(LeftContactX + 0.55 / 60, 0.75, -0.55, 0);

        game.Advance(1.0 / 60, 0, 0);
        await Assert.That(game.BallVx < 0).IsTrue();

        game.Advance(1.0 / 60, 1, 0);
        await Assert.That(game.BallVx < 0).IsTrue();
        await Assert.That(game.BallX < LeftContactX).IsTrue();
    }

    [Test]
    public async Task Advance_WhenOneStepContainsRepeatedPaddleContacts_ResolvesEachContact()
    {
        var game = Playing(0.5, 0.5, 1.5, 0);

        game.Advance(4, 0, 0);

        await Assert.That(game.Phase).IsEqualTo(GamePhase.Playing);
        await Assert.That(game.LeftScore).IsEqualTo(0);
        await Assert.That(game.RightScore).IsEqualTo(0);
        await Assert.That(game.BallVx).IsEqualTo(-0.655).Within(1e-10);
        await Assert.That(game.BallY).IsEqualTo(0.5).Within(1e-10);
    }

    private static GameEngine Playing(
        double ballX, double ballY, double vx, double vy,
        double leftY = 0.5, double rightY = 0.5)
    {
        var game = new GameEngine();
        game.Restore(new GameState
        {
            Phase = GamePhase.Playing,
            BallX = ballX,
            BallY = ballY,
            BallVx = vx,
            BallVy = vy,
            LeftY = leftY,
            RightY = rightY
        });
        return game;
    }

}
