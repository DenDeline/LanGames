namespace LanPong.Tests;

public sealed class PaddleSideTests
{
    [Test]
    public async Task ResolvedSides_HaveDeliberateOrdinalsAndOpposites()
    {
        var ordinals = Enum.GetValues<PaddleSide>().Select(side => (int)side);
        await Assert.That(string.Join(',', ordinals)).IsEqualTo("1,2");
        await Assert.That(PaddleSides.IsPhysical(PaddleSide.Left)).IsTrue();
        await Assert.That(PaddleSides.IsPhysical(PaddleSide.Right)).IsTrue();
        await Assert.That(PaddleSides.Opposite(PaddleSide.Left)).IsEqualTo(PaddleSide.Right);
        await Assert.That(PaddleSides.Opposite(PaddleSide.Right)).IsEqualTo(PaddleSide.Left);
    }

    [Test]
    [Arguments((PaddleSide)0)]
    [Arguments((PaddleSide)3)]
    [Arguments((PaddleSide)(-1))]
    public async Task ResolvedSide_RejectsDefaultAndNonphysicalValuesBeforeAdvancing(PaddleSide invalidSide)
    {
        var game = StartedMatch();
        var before = game.CaptureCheckpoint();

        await Assert.That(PaddleSides.IsPhysical(invalidSide)).IsFalse();
        await Assert.That(() => PaddleSides.Validate(invalidSide)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => PaddleSides.Opposite(invalidSide)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => game.AdvanceForSide(GameConstants.FixedStepSeconds, invalidSide, 1, -1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(game.CaptureCheckpoint()).IsEqualTo(before);
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task AdvanceForSide_RoutesBothAxesIntoThePhysicalWorld(PaddleSide localSide)
    {
        var game = StartedMatch();
        var physical = StartedMatch();

        game.AdvanceForSide(GameConstants.FixedStepSeconds, localSide, localAxis: 1, oppositeAxis: -1);
        physical.Advance(GameConstants.FixedStepSeconds,
            localSide == PaddleSide.Left ? 1 : -1, localSide == PaddleSide.Left ? -1 : 1);

        await Assert.That(game.CaptureCheckpoint()).IsEqualTo(physical.CaptureCheckpoint());
        await Assert.That(localSide == PaddleSide.Left ? game.LeftY > 0.5 : game.RightY > 0.5).IsTrue();
        await Assert.That(localSide == PaddleSide.Left ? game.RightY < 0.5 : game.LeftY < 0.5).IsTrue();
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task HostTimeline_MapsHostAndGuestAxesDuringNormalAdvance(PaddleSide hostSide)
    {
        var game = StartedMatch();
        var physical = StartedMatch();
        var timeline = new HostRollbackTimeline(game);
        timeline.Reset(hostSide);
        timeline.Receive(new InputPacket { Tick = 1, Sequence = 1, RoundId = game.RoundId, Axes = [-1] });

        timeline.Advance(1);
        physical.Advance(GameConstants.FixedStepSeconds,
            hostSide == PaddleSide.Left ? 1 : -1, hostSide == PaddleSide.Left ? -1 : 1);

        await Assert.That(game.CaptureCheckpoint()).IsEqualTo(physical.CaptureCheckpoint());
    }

    [Test]
    [Arguments(PaddleSide.Left)]
    [Arguments(PaddleSide.Right)]
    public async Task GuestTimeline_MapsHostAndGuestAxesDuringReconciliationAndAdvance(PaddleSide hostSide)
    {
        var host = StartedMatch();
        var guest = new GameEngine();
        var physical = new GameEngine();
        physical.Restore(host.Capture());
        var timeline = new GuestPredictionTimeline(guest);
        timeline.Reset(hostSide);

        timeline.Reconcile(host.Capture(), hostAxis: 1, pingMs: 60, localAxis: -1);
        for (var tick = 0; tick < guest.TickNumber; tick++)
            physical.Advance(GameConstants.FixedStepSeconds,
                hostSide == PaddleSide.Left ? 1 : -1, hostSide == PaddleSide.Left ? -1 : 1);
        await Assert.That(guest.CaptureCheckpoint()).IsEqualTo(physical.CaptureCheckpoint());

        timeline.Advance(-1);
        physical.Advance(GameConstants.FixedStepSeconds,
            hostSide == PaddleSide.Left ? 1 : -1, hostSide == PaddleSide.Left ? -1 : 1);
        await Assert.That(guest.CaptureCheckpoint()).IsEqualTo(physical.CaptureCheckpoint());
        await Assert.That(hostSide == PaddleSide.Left ? guest.RightY < 0.5 : guest.LeftY < 0.5).IsTrue();
        await Assert.That(hostSide == PaddleSide.Left ? guest.LeftY > 0.5 : guest.RightY > 0.5).IsTrue();
    }

    private static GameEngine StartedMatch()
    {
        var game = new GameEngine();
        game.StartMatch();
        return game;
    }
}
