using System.Text.Json;
using LanPong.Bots.Catalog;
using LanPong.Bots.Inference;
using LanPong.Bots.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanPong.Tests;

public sealed class SideLifecycleTests
{
    [Test]
    public async Task BotAdmission_ResolvesOnlyRandomAfterSuccessfulPreparationAndResetsOnLeave()
    {
        foreach (var outcome in new[] { PaddleSide.Left, PaddleSide.Right })
        {
            var draws = 0;
            var created = new List<TrackedBotController>();
            var runtime = BotTestSupport.Runtime([BotTestSupport.Tracker()], Factory(_ =>
            {
                var controller = new TrackedBotController();
                created.Add(controller);
                return controller;
            }));
            await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime,
                () => { draws++; return outcome; });
            foreach (var preference in new[] { InitialSidePreference.Left, InitialSidePreference.Right,
                         InitialSidePreference.Random })
            {
                var before = draws;
                var rejected = await BotTestSupport.CaptureAsync(() =>
                    peer.StartBotAsync("Human", "unknown", preference));
                await Assert.That(rejected).IsNotNull();
                await Assert.That(draws).IsEqualTo(before);
                await Assert.That(peer.Snapshot().LocalSide).IsNull();
                await Assert.That(peer.Snapshot().MatchId).IsNull();
                await peer.StartBotAsync("Human", "tracker", preference);
                var admitted = peer.Snapshot();
                var expected = preference == InitialSidePreference.Random ? outcome : (PaddleSide)preference;
                await Assert.That(admitted.LocalSide).IsEqualTo(expected);
                await Assert.That(draws).IsEqualTo(before + (preference == InitialSidePreference.Random ? 1 : 0));
                await Assert.That(Guid.TryParseExact(admitted.MatchId, "N", out _)).IsTrue();
                var activeRejected = await BotTestSupport.CaptureAsync(() =>
                    peer.StartBotAsync("Human", "tracker", InitialSidePreference.Random));
                await Assert.That(activeRejected is InvalidOperationException).IsTrue();
                await Assert.That(draws).IsEqualTo(before + (preference == InitialSidePreference.Random ? 1 : 0));
                await peer.LeaveAsync();
                await Assert.That(peer.Snapshot().LocalSide).IsNull();
                await Assert.That(peer.Snapshot().MatchId).IsNull();
            }
            await Assert.That(draws).IsEqualTo(1);
            await Assert.That(created.All(controller => controller.DisposeCount == 1)).IsTrue();
        }
    }

    [Test]
    public async Task BotRematch_RetainsFinishedOrientationThenAlternatesOnceWithWarmIdentityAndInputFence()
    {
        foreach (var initial in new[] { InitialSidePreference.Left, InitialSidePreference.Right })
        {
            var opponent = new TrackedBotController(0);
            var factory = Factory(_ => opponent);
            await using var peer = new PongPeer(NullLogger<PongPeer>.Instance,
                BotTestSupport.Runtime([BotTestSupport.Tracker()], factory));
            await peer.StartBotAsync("Human", "tracker", initial);
            var start = peer.Snapshot();
            await Assert.That(start.CanRematch).IsFalse();
            var matchId = Guid.ParseExact(start.MatchId!, "N");
            var controller = Guid.NewGuid();
            await Assert.That(() => peer.Restart(matchId, start.RoundId)).Throws<InvalidOperationException>();
            peer.SetInput(controller, matchId, start.RoundId, 1);
            await BotTestSupport.WaitForAsync(peer, snapshot => (initial == InitialSidePreference.Left
                ? snapshot.LeftY : snapshot.RightY) > 0.52);
            var finished = await PeerTestAccess.FinishAsync(peer);
            await Assert.That(finished.CanRematch).IsTrue();
            var identity = peer.BotStatus;
            await Task.Delay(35);
            var stillFinished = peer.Snapshot();
            await Assert.That(stillFinished.LocalSide).IsEqualTo(start.LocalSide);
            await Assert.That(stillFinished.LeftScore).IsEqualTo(GameConstants.WinningScore);
            await Assert.That(stillFinished.RightScore).IsEqualTo(2);
            await Assert.That(() => peer.Restart(Guid.NewGuid(), finished.RoundId)).Throws<InvalidOperationException>();
            await Assert.That(() => peer.Restart(matchId, 0)).Throws<InvalidOperationException>();

            var requests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                try { peer.Restart(matchId, finished.RoundId); return true; }
                catch (InvalidOperationException) { return false; }
            })));
            await Assert.That(requests.Count(success => success)).IsEqualTo(1);
            var rematch = peer.Snapshot();
            await Assert.That(rematch.CanRematch).IsFalse();
            await Assert.That(rematch.RoundId).IsEqualTo(finished.RoundId + 1);
            await Assert.That(rematch.LocalSide).IsEqualTo(PaddleSides.Opposite(start.LocalSide!.Value));
            await Assert.That(rematch.MatchId).IsEqualTo(start.MatchId);
            await Assert.That(rematch.LeftScore).IsEqualTo(0);
            await Assert.That(rematch.RightScore).IsEqualTo(0);
            await Assert.That(peer.BotStatus).IsEqualTo(identity);
            await Assert.That(opponent.ResetCount).IsEqualTo(2);
            await Assert.That(factory.Definitions.Count).IsEqualTo(1);

            // Queued previous-round and foreign-session input cannot re-hold the newly owned paddle.
            peer.SetInput(controller, matchId, finished.RoundId, 1);
            peer.SetInput(controller, Guid.NewGuid(), rematch.RoundId, 1);
            await Task.Delay(50);
            await Assert.That(peer.Snapshot().LeftY).IsEqualTo(GameConstants.ArenaCenter);
            await Assert.That(peer.Snapshot().RightY).IsEqualTo(GameConstants.ArenaCenter);
            peer.SetInput(controller, matchId, rematch.RoundId, -1);
            await BotTestSupport.WaitForAsync(peer, snapshot => (rematch.LocalSide == PaddleSide.Left
                ? snapshot.LeftY : snapshot.RightY) < 0.48);
            var secondFinish = await PeerTestAccess.FinishAsync(peer);
            await Assert.That(() => peer.Restart(matchId, finished.RoundId)).Throws<InvalidOperationException>();
            peer.Restart(matchId, secondFinish.RoundId);
            await Assert.That(peer.Snapshot().LocalSide).IsEqualTo(start.LocalSide);
            await Assert.That(peer.Snapshot().RoundId).IsEqualTo(start.RoundId + 2);
            await Assert.That(opponent.ResetCount).IsEqualTo(3);
            await peer.LeaveAsync();
            await Assert.That(opponent.DisposeCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task LanAdmissionAndSimultaneousRematch_UseSingleDrawOppositeSidesAndRoundBoundRetry()
    {
        foreach (var side in new[] { PaddleSide.Left, PaddleSide.Right })
        {
            var draws = 0;
            await using var host = Peer(() => { draws++; return side; });
            await using var guest = Peer(() => throw new InvalidOperationException("A guest cannot draw sides."));
            await host.HostAsync(BotTestSupport.ReservePort(), "Host");
            await Assert.That(host.Snapshot().LocalSide).IsNull();
            await Assert.That(host.Snapshot().MatchId).IsNull();
            await guest.JoinAsync("127.0.0.1", host.Snapshot().UdpPort, "Guest");
            await BotTestSupport.WaitForAsync(host, snapshot => snapshot.Connection == ConnectionState.IncomingChallenge);
            await Assert.That(host.Snapshot().LocalSide).IsNull();
            await Assert.That(draws).IsEqualTo(0);
            await host.AcceptChallengeAsync();
            var joined = await BotTestSupport.WaitForAsync(guest, snapshot =>
                snapshot.Connection == ConnectionState.Connected && snapshot.Tick > 0);
            var admitted = host.Snapshot();
            await Assert.That(draws).IsEqualTo(1);
            await Assert.That(admitted.LocalSide).IsEqualTo(side);
            await Assert.That(joined.LocalSide).IsEqualTo(PaddleSides.Opposite(side));
            await Assert.That(joined.MatchId).IsEqualTo(admitted.MatchId);
            var matchId = Guid.ParseExact(admitted.MatchId!, "N");
            var guestController = Guid.NewGuid();
            guest.SetInput(guestController, matchId, joined.RoundId, 1);
            await BotTestSupport.WaitForAsync(host, snapshot => (side == PaddleSide.Right
                ? snapshot.LeftY : snapshot.RightY) > 0.52);
            var finished = await PeerTestAccess.FinishAsync(host);
            await BotTestSupport.WaitForAsync(guest, snapshot => snapshot.Phase == GamePhase.GameOver &&
                PeerTestAccess.Field<int>(guest, "_confirmedGuestFinishedRound") == snapshot.RoundId);
            await Assert.That(guest.Snapshot().LocalSide).IsEqualTo(joined.LocalSide);
            await Assert.That(guest.Snapshot().LeftScore).IsEqualTo(finished.LeftScore);
            await Task.WhenAll(Task.Run(() =>
            {
                try { host.Restart(matchId, finished.RoundId); }
                catch (InvalidOperationException) { /* Guest request may win the race. */ }
            }), Task.Run(() =>
            {
                try { guest.Restart(matchId, finished.RoundId); }
                catch (InvalidOperationException) { /* Host state may win the race. */ }
            }));
            var rematched = await BotTestSupport.WaitForAsync(guest, snapshot => snapshot.RoundId == finished.RoundId + 1);
            await Assert.That(host.Snapshot().LocalSide).IsEqualTo(PaddleSides.Opposite(side));
            await Assert.That(rematched.LocalSide).IsEqualTo(side);
            await Assert.That(rematched.MatchId).IsEqualTo(joined.MatchId);
            await Assert.That(draws).IsEqualTo(1);
            var request = new RestartPacket { SessionId = matchId, RequestId = Guid.NewGuid(), ExpectedRoundId = finished.RoundId };
            PeerTestAccess.DeliverHostSession(host, request);
            await Assert.That(host.Snapshot().RoundId).IsEqualTo(finished.RoundId + 1);
            var nextFinish = await PeerTestAccess.FinishAsync(host);
            PeerTestAccess.DeliverHostSession(host, request with { RequestId = Guid.NewGuid() });
            await Assert.That(host.Snapshot().RoundId).IsEqualTo(nextFinish.RoundId);
            await BotTestSupport.WaitForAsync(guest, snapshot => snapshot.Phase == GamePhase.GameOver &&
                PeerTestAccess.Field<int>(guest, "_confirmedGuestFinishedRound") == snapshot.RoundId);
            PeerTestAccess.UnderGate(guest, () =>
            {
                guest.Restart(matchId, nextFinish.RoundId);
                var pendingId = PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId");
                guest.Restart(matchId, nextFinish.RoundId);
                if (PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId") != pendingId ||
                    guest.Snapshot().RoundId != nextFinish.RoundId ||
                    guest.Snapshot().LocalSide != rematched.LocalSide)
                    throw new InvalidOperationException("Pending guest rematch changed request identity, round or orientation.");
            });
            await BotTestSupport.WaitForAsync(host, snapshot => snapshot.RoundId == nextFinish.RoundId + 1);
            await BotTestSupport.WaitForAsync(guest, snapshot => snapshot.RoundId == nextFinish.RoundId + 1);
            await Assert.That(host.Snapshot().LocalSide).IsEqualTo(side);
            await Assert.That(draws).IsEqualTo(1);
            await guest.LeaveAsync();
            await BotTestSupport.WaitForAsync(host, snapshot => snapshot.Connection == ConnectionState.Waiting);
            await Assert.That(host.Snapshot().LocalSide).IsNull();
            await Assert.That(host.Snapshot().MatchId).IsNull();
            await guest.JoinAsync("127.0.0.1", host.Snapshot().UdpPort, "Guest again");
            await BotTestSupport.WaitForAsync(host, snapshot => snapshot.Connection == ConnectionState.IncomingChallenge);
            await host.AcceptChallengeAsync();
            var rejoined = await BotTestSupport.WaitForAsync(guest, snapshot => snapshot.Connection == ConnectionState.Connected);
            await Assert.That(draws).IsEqualTo(2);
            await Assert.That(rejoined.MatchId == admitted.MatchId).IsFalse();
        }
    }

    [Test]
    public async Task GuestRematch_RejectsPredictedFinishAndCancelsIntentOnAuthoritativePlayingCorrection()
    {
        await using var guest = Peer();
        await guest.JoinAsync("127.0.0.1", 9, "Guest");
        var challenge = PeerTestAccess.Field<Guid?>(guest, "_outgoingChallengeId")!.Value;
        var session = Guid.NewGuid();
        var host = new GameEngine();
        host.StartMatch();
        var nearWinning = host.Capture() with
        {
            Phase = GamePhase.Playing, TickNumber = 90, Countdown = 0,
            LeftScore = GameConstants.WinningScore - 1, RightScore = 2,
            BallX = 1, BallY = 0.5, BallVx = 1, BallVy = 0
        };
        host.RestoreCheckpoint(nearWinning);
        PeerTestAccess.DeliverGuest(guest, new WelcomePacket
        {
            SessionId = session, RequestId = challenge, Nickname = "Host",
            HostSide = PaddleSide.Right, RoundId = host.RoundId
        });
        PeerTestAccess.DeliverGuest(guest, AuthoritativeState(session, PaddleSide.Right, host.Capture()));
        var predicted = await BotTestSupport.WaitForAsync(guest, snapshot => snapshot.Phase == GamePhase.GameOver);
        await Assert.That(host.Phase).IsEqualTo(GamePhase.Playing);
        await Assert.That(predicted.LeftScore).IsEqualTo(GameConstants.WinningScore);
        await AssertPublicRematchCapabilityAsync(predicted, expected: false);
        await Assert.That(PeerTestAccess.Field<int>(guest, "_confirmedGuestFinishedRound")).IsEqualTo(0);
        await Assert.That(() => guest.Restart(session, predicted.RoundId)).Throws<InvalidOperationException>();
        await Assert.That(PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId")).IsNull();
        await Assert.That(PeerTestAccess.Field<int>(guest, "_restartAfterRound")).IsEqualTo(0);

        // The authoritative simulation now produces the real seventh goal. A new explicit
        // request can queue, while duplicate requests retain its identity and completed sides.
        host.Advance(GameConstants.FixedStepSeconds, 0, 0);
        await Assert.That(host.Phase).IsEqualTo(GamePhase.GameOver);
        PeerTestAccess.DeliverGuest(guest, AuthoritativeState(session, PaddleSide.Right, host.Capture()));
        await AssertPublicRematchCapabilityAsync(guest.Snapshot(), expected: true);
        await Assert.That(PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId")).IsNull();
        Guid? firstRequest = null;
        Guid? repeatedRequest = null;
        PongSnapshot? corrected = null;
        PeerTestAccess.UnderGate(guest, () =>
        {
            guest.Restart(session, host.RoundId);
            firstRequest = PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId");
            guest.Restart(session, host.RoundId);
            repeatedRequest = PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId");

            // A same-round rollback can revoke a previously confirmed finish. Keep enough
            // speculative lead to score seven again during reconciliation of raw Playing.
            for (var tick = 0; tick < 8; tick++)
                PeerTestAccess.Field<GuestPredictionTimeline>(guest, "_guestTimeline").Advance(0);
            host.RestoreCheckpoint(nearWinning with { TickNumber = host.TickNumber + 1 });
            PeerTestAccess.DeliverGuest(guest, AuthoritativeState(session, PaddleSide.Right, host.Capture()));
            corrected = guest.Snapshot();
        });
        await Assert.That(firstRequest).IsNotNull();
        await Assert.That(repeatedRequest).IsEqualTo(firstRequest);
        await Assert.That(host.Phase).IsEqualTo(GamePhase.Playing);
        await Assert.That(corrected!.Phase).IsEqualTo(GamePhase.GameOver); // Predicted terminal replay persists.
        await AssertPublicRematchCapabilityAsync(corrected, expected: false);
        await Assert.That(corrected.LocalSide).IsEqualTo(predicted.LocalSide);
        await Assert.That(corrected.RoundId).IsEqualTo(predicted.RoundId);
        await Assert.That(PeerTestAccess.Field<int>(guest, "_confirmedGuestFinishedRound")).IsEqualTo(0);
        await Assert.That(PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId")).IsNull();
        await Assert.That(PeerTestAccess.Field<int>(guest, "_restartAfterRound")).IsEqualTo(0);
        await Assert.That(PeerTestAccess.Field<DateTime>(guest, "_lastRestartSent")).IsEqualTo(DateTime.MinValue);
        await Assert.That(() => guest.Restart(session, host.RoundId)).Throws<InvalidOperationException>();

        host.Advance(GameConstants.FixedStepSeconds, 0, 0);
        await Assert.That(host.Phase).IsEqualTo(GamePhase.GameOver);
        PeerTestAccess.DeliverGuest(guest, AuthoritativeState(session, PaddleSide.Right, host.Capture()));
        await AssertPublicRematchCapabilityAsync(guest.Snapshot(), expected: true);
        await Task.Delay(35);
        await Assert.That(PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId")).IsNull();
        await Assert.That(PeerTestAccess.Field<int>(guest, "_restartAfterRound")).IsEqualTo(0);
        await Assert.That(guest.Snapshot().RoundId).IsEqualTo(predicted.RoundId);
        guest.Restart(session, host.RoundId); // Only a fresh user request restores rematch intent.
        await Assert.That(PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId")).IsNotNull();
        await Assert.That(PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId") == firstRequest).IsFalse();
        await guest.LeaveAsync();
        await Assert.That(PeerTestAccess.Field<int>(guest, "_confirmedGuestFinishedRound")).IsEqualTo(0);
        await Assert.That(PeerTestAccess.Field<Guid?>(guest, "_pendingRestartRequestId")).IsNull();
    }

    [Test]
    public async Task GuestWelcomeAndState_ReplaceUnrelatedRoundAndRejectRewindSideDriftAndOldWelcome()
    {
        await using var guest = Peer();
        // The engine deliberately preserves RoundId on leave. Create a genuine unrelated round history.
        for (var index = 0; index < 5; index++)
        {
            await guest.StartBotAsync("Human", "tracker", InitialSidePreference.Left);
            await guest.LeaveAsync();
        }
        await guest.JoinAsync("127.0.0.1", 9, "Guest");
        var challenge = PeerTestAccess.Field<Guid?>(guest, "_outgoingChallengeId")!.Value;
        var session = Guid.NewGuid();
        var welcome = new WelcomePacket
        {
            SessionId = session, RequestId = challenge, Nickname = "Host", HostSide = PaddleSide.Right, RoundId = 2
        };
        PeerTestAccess.DeliverGuest(guest, welcome);
        await Assert.That(guest.Snapshot().RoundId).IsEqualTo(2);
        await Assert.That(guest.Snapshot().LocalSide).IsEqualTo(PaddleSide.Left);
        var state = new StatePacket
        {
            SessionId = session, Sequence = 10, RoundId = 2, HostSide = PaddleSide.Right,
            LeftY = 0.5, RightY = 0.5, BallX = 0.5, BallY = 0.5, Phase = GamePhase.Countdown,
            Countdown = 1, ServeDirection = 1, RecentEvents = []
        };
        PeerTestAccess.DeliverGuest(guest, state);
        var held = Guid.NewGuid();
        guest.SetInput(held, session, 2, 1);
        PeerTestAccess.DeliverGuest(guest, state with { Sequence = 10000, RoundId = 1, HostSide = PaddleSide.Left });
        PeerTestAccess.DeliverGuest(guest, state with { Sequence = 10000, HostSide = PaddleSide.Left });
        await Assert.That(PeerTestAccess.Field<long>(guest, "_lastStateSequence")).IsEqualTo(10);
        await Assert.That(guest.Snapshot().RoundId).IsEqualTo(2);
        PeerTestAccess.DeliverGuest(guest, state with { Sequence = 20, RoundId = 3, HostSide = PaddleSide.Left });
        await Assert.That(guest.Snapshot().LocalSide).IsEqualTo(PaddleSide.Right);
        await Assert.That(PeerTestAccess.Field<LocalControllerInputs>(guest, "_controllers").GetAxis(DateTime.UtcNow)).IsEqualTo(0);
        PeerTestAccess.DeliverGuest(guest, welcome); // Late duplicate cannot undo round 3.
        await Assert.That(guest.Snapshot().RoundId).IsEqualTo(3);
        PeerTestAccess.DeliverGuest(guest, state with { Sequence = 30, RoundId = 5, HostSide = PaddleSide.Left });
        await Assert.That(guest.Snapshot().RoundId).IsEqualTo(5);
        await Assert.That(guest.Snapshot().LocalSide).IsEqualTo(PaddleSide.Right);
        PeerTestAccess.DeliverGuest(guest, state with { Sequence = 40, RoundId = 6, HostSide = PaddleSide.Left });
        await Assert.That(guest.Snapshot().RoundId).IsEqualTo(5);
        PeerTestAccess.DeliverGuest(guest, state with { Sequence = 40, RoundId = 6, HostSide = PaddleSide.Right });
        await Assert.That(guest.Snapshot().RoundId).IsEqualTo(6);
        await Assert.That(guest.Snapshot().LocalSide).IsEqualTo(PaddleSide.Left);
    }

    [Test]
    public async Task LiveLeftBot_RealOnnxConsumesMirroredApproachAndKeepsFrozenModelWarm()
    {
        OnnxRuntimeStartup.DisablePosixTelemetry();
        RecordingOrt? inference = null;
        OnnxLocalOpponentController? model = null;
        var factory = new TestBotFactory(BotStrategyDescriptor.OnnxId, BotSettingsKind.Onnx, entry =>
            model = new OnnxLocalOpponentController(entry.Onnx!, path => inference = new RecordingOrt(path)));
        var runtime = BotTestSupport.Runtime([BotTestSupport.Onnx(path: BotTestSupport.ModelPath())], factory);
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);
        await peer.StartBotAsync("Human right", "model", InitialSidePreference.Right);
        var world = new GameState
        {
            RoundId = peer.Snapshot().RoundId, TickNumber = 8, Phase = GamePhase.Playing,
            LeftY = 0.5, RightY = 0.3, BallX = 0.2, BallY = 0.8,
            BallVx = -0.55, BallVy = 0, LeftScore = 2, RightScore = 5, ServeDirection = -1,
            RecentEvents = GameEventHistory.Empty
        };
        PeerTestAccess.UnderGate(peer, () => PeerTestAccess.Field<GameEngine>(peer, "_game").RestoreCheckpoint(world));
        var (observed, axis) = await inference!.First.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var physical = new GameEngine();
        physical.RestoreCheckpoint(world);
        physical.Advance(GameConstants.FixedStepSeconds, 0, 0); // Tick 8 holds warmed axis zero.
        var canonical = physical.Capture();
        var expectedView = canonical with
        {
            LeftY = canonical.RightY, RightY = canonical.LeftY, BallX = 1 - canonical.BallX,
            BallVx = -canonical.BallVx, LeftScore = canonical.RightScore, RightScore = canonical.LeftScore,
            ServeDirection = -canonical.ServeDirection
        };
        await Assert.That(observed.SequenceEqual(RightBotObservationV1.Encode(expectedView))).IsTrue();
        await Assert.That(axis).IsEqualTo(1);
        await BotTestSupport.WaitForAsync(peer, snapshot => snapshot.LeftY > 0.51);
        await Assert.That(peer.Snapshot().RightY).IsEqualTo(0.3);
        await Assert.That(peer.Snapshot().BallVx).IsLessThan(0);
        await Assert.That(peer.Snapshot().RequestedBotId).IsEqualTo("model");
        await Assert.That(peer.Snapshot().EffectiveBotId).IsEqualTo("model");
        await Assert.That(peer.Snapshot().OpponentFallbackActive).IsFalse();
        await Assert.That(model!.ModelSha256).IsEqualTo(BotModelV1.ExpectedSha256);
        await Assert.That(PeerTestAccess.Field<LanPong.Bots.Runtime.PreparedBotSession>(peer, "_botSession").VerifiedModelSha256)
            .IsEqualTo(BotModelV1.ExpectedSha256);
        var finished = await PeerTestAccess.FinishAsync(peer);
        peer.Restart(Guid.ParseExact(finished.MatchId!, "N"), finished.RoundId);
        await Assert.That(peer.Snapshot().LocalSide).IsEqualTo(PaddleSide.Left);
        await Assert.That(factory.Definitions.Count).IsEqualTo(1);
        await Assert.That(inference.DisposeCount).IsEqualTo(0);
        await peer.LeaveAsync();
        await Assert.That(inference.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task LiveLeftFallback_ReceivesTheSameMirroredApproachAndRoutesPositiveAxisLeft()
    {
        RecordingTracker? fallback = null;
        var failed = new FailingPlayingPolicy();
        var factory = Factory(entry => entry.Id == "primary" ? failed : fallback = new RecordingTracker(entry.Tracker!));
        var runtime = BotTestSupport.Runtime([
            BotTestSupport.Tracker("primary", fallback: "rescue"), BotTestSupport.Tracker("rescue")
        ], factory);
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);
        await peer.StartBotAsync("Human right", "primary", InitialSidePreference.Right);
        var world = new GameState
        {
            RoundId = peer.Snapshot().RoundId, TickNumber = 90, Phase = GamePhase.Playing,
            LeftY = 0.5, RightY = 0.3, BallX = 0.2, BallY = 0.8,
            BallVx = -0.55, BallVy = 0, LeftScore = 2, RightScore = 5, ServeDirection = -1,
            RecentEvents = GameEventHistory.Empty
        };
        PeerTestAccess.UnderGate(peer, () => PeerTestAccess.Field<GameEngine>(peer, "_game").RestoreCheckpoint(world));
        var (view, axis) = await fallback!.First.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(view).IsEqualTo(world with
        {
            LeftY = 0.3, RightY = 0.5, BallX = 0.8, BallVx = 0.55,
            LeftScore = 5, RightScore = 2, ServeDirection = 1
        });
        await Assert.That(failed.First).IsEqualTo(view);
        await Assert.That(axis).IsEqualTo(1);
        await BotTestSupport.WaitForAsync(peer, snapshot => snapshot.LeftY > 0.52 && failed.DisposeCount == 1);
        await Assert.That(peer.Snapshot().RightY).IsEqualTo(0.3);
        await Assert.That(peer.Snapshot().BallVx).IsLessThan(0);
        await Assert.That(peer.Snapshot().RequestedBotId).IsEqualTo("primary");
        await Assert.That(peer.Snapshot().EffectiveBotId).IsEqualTo("rescue");
        await Assert.That(peer.Snapshot().OpponentFallbackActive).IsTrue();
        await Assert.That(factory.Definitions.Count).IsEqualTo(2);
        await peer.LeaveAsync();
        await Assert.That(failed.DisposeCount).IsEqualTo(1);
        await Assert.That(fallback.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task RequiredJsonRequests_RejectMissingUnknownNumericAndNonstringChoicesAndMissingFences()
    {
        foreach (var choice in new[] { "left", "right", "random" })
        {
            var request = JsonSerializer.Deserialize($$"""{"nickname":"Human","botId":"tracker","side":"{{choice}}"}""",
                AppJsonSerializerContext.Default.LocalOpponentRequest)!;
            await Assert.That(request.Side).IsEqualTo(choice switch
            {
                "left" => InitialSidePreference.Left, "right" => InitialSidePreference.Right, _ => InitialSidePreference.Random
            });
        }
        foreach (var value in new[] { "null", "0", "1", "2", "3", "true", "{}", "[]", "\"randomized\"", "\"left,right\"", "\"left,left\"", "\"LEFT\"", "\"\"" })
            await Assert.That(() => JsonSerializer.Deserialize($$"""{"nickname":"Human","botId":"tracker","side":{{value}}}""",
                AppJsonSerializerContext.Default.LocalOpponentRequest)).Throws<JsonException>();
        await Assert.That(() => JsonSerializer.Deserialize("{\"nickname\":\"Human\",\"botId\":\"tracker\"}",
            AppJsonSerializerContext.Default.LocalOpponentRequest)).Throws<JsonException>();
        foreach (var missing in new[] { "{}", "{\"matchId\":\"ffeeddccbbaa99887766554433221100\"}", "{\"expectedRoundId\":1}" })
            await Assert.That(() => JsonSerializer.Deserialize(missing, AppJsonSerializerContext.Default.RestartRequest)).Throws<JsonException>();
        var valid = JsonSerializer.Deserialize("{\"matchId\":\"ffeeddccbbaa99887766554433221100\",\"expectedRoundId\":1}",
            AppJsonSerializerContext.Default.RestartRequest)!;
        await Assert.That(valid.ExpectedRoundId).IsEqualTo(1);
        foreach (var invalidSide in new[] { "null", "0", "1", "3", "true", "\"left,right\"", "\"left,left\"", "\"random\"" })
            await Assert.That(() => JsonSerializer.Deserialize<PaddleSide>(invalidSide)).Throws<JsonException>();
        foreach (var invalid in new[] { (PaddleSide)0, (PaddleSide)3 })
            await Assert.That(() => JsonSerializer.Serialize(invalid)).Throws<JsonException>();
        foreach (var side in new[] { PaddleSide.Left, PaddleSide.Right })
        {
            var json = JsonSerializer.Serialize(new PongSnapshot(PeerRole.Host, ConnectionState.Connected, "", 0,
                [], null, 0.5, 0.5, 0.5, 0.5, 0, 0, 0, 0, GamePhase.Playing, 0, 1, 1, null, [],
                "Human", null, OpponentMode.Bot, LocalSide: side), AppJsonSerializerContext.Default.PongSnapshot);
            using var parsed = JsonDocument.Parse(json);
            await Assert.That(parsed.RootElement.GetProperty("localSide").GetString()).IsEqualTo(side == PaddleSide.Left ? "left" : "right");
        }
    }

    [Test]
    public async Task SnapshotCaptureSequence_OrdersHttpAndWebSocketAcrossLeaveAdmissionAndRematch()
    {
        await using var peer = Peer();
        var idle = peer.Snapshot();
        await Assert.That(Guid.TryParseExact(idle.SourceId, "N", out _)).IsTrue();
        await Assert.That(idle.SnapshotSequence).IsGreaterThan(0);
        await peer.StartBotAsync("Human", "tracker", InitialSidePreference.Right);
        var started = peer.Snapshot();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        BrowserWebSocketProtocol.WriteSnapshot(started, buffer);
        var decoded = BrowserWebSocketProtocolTests.ReadSnapshot(buffer.WrittenMemory).Snapshot;
        await Assert.That(decoded.SourceId).IsEqualTo(idle.SourceId);
        await Assert.That(decoded.SnapshotSequence).IsEqualTo(started.SnapshotSequence);
        await Assert.That(started.SnapshotSequence).IsGreaterThan(idle.SnapshotSequence);
        var finished = await PeerTestAccess.FinishAsync(peer);
        peer.Restart(Guid.ParseExact(finished.MatchId!, "N"), finished.RoundId);
        var rematched = peer.Snapshot();
        await Assert.That(rematched.SourceId).IsEqualTo(idle.SourceId);
        await Assert.That(rematched.SnapshotSequence).IsGreaterThan(finished.SnapshotSequence);
        await peer.LeaveAsync();
        var left = peer.Snapshot();
        await Assert.That(left.SourceId).IsEqualTo(idle.SourceId);
        await Assert.That(left.SnapshotSequence).IsGreaterThan(rematched.SnapshotSequence);
        await Assert.That(left.MatchId).IsNull();
        await using var other = Peer();
        await Assert.That(other.Snapshot().SourceId == idle.SourceId).IsFalse();
    }

    private sealed class RecordingOrt(string path) : IOnnxInferenceSession
    {
        private readonly OrtOnnxInferenceSession _inner = new(path);
        public TaskCompletionSource<(float[] Observation, int Axis)> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public void Run(ReadOnlySpan<float> observation, Span<float> logits)
        {
            _inner.Run(observation, logits);
            if (!First.Task.IsCompleted)
            {
                var best = 1;
                for (var candidate = 0; candidate < logits.Length; candidate++)
                    if (logits[candidate] > logits[best]) best = candidate;
                First.TrySetResult((observation.ToArray(), RightBotObservationV1.AxisFromClass(best)));
            }
        }
        public void Dispose() { DisposeCount++; _inner.Dispose(); }
    }

    private sealed class RecordingTracker(TrackerBotSettings settings) : ILocalOpponentController, IDisposable
    {
        private readonly TrackerBotPolicy _inner = new(settings);
        public TaskCompletionSource<(GameState State, int Axis)> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public void Reset() => _inner.Reset();
        public int GetAxis(GameState state)
        {
            var axis = _inner.GetAxis(state);
            if (state.Phase == GamePhase.Playing) First.TrySetResult((state, axis));
            return axis;
        }
        public void Dispose() => DisposeCount++;
    }

    private sealed class FailingPlayingPolicy : ILocalOpponentController, IDisposable
    {
        public GameState? First { get; private set; }
        public int DisposeCount { get; private set; }
        public void Reset() { }
        public int GetAxis(GameState state)
        {
            if (state.Phase != GamePhase.Playing) return 0;
            First = state;
            throw new InvalidDataException("Synthetic live failure.");
        }
        public void Dispose() => DisposeCount++;
    }

    private static async Task AssertPublicRematchCapabilityAsync(PongSnapshot snapshot, bool expected)
    {
        await Assert.That(snapshot.CanRematch).IsEqualTo(expected);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, AppJsonSerializerContext.Default.PongSnapshot));
        var property = json.RootElement.GetProperty("canRematch");
        await Assert.That(property.ValueKind).IsEqualTo(expected ? JsonValueKind.True : JsonValueKind.False);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        BrowserWebSocketProtocol.WriteSnapshot(snapshot, buffer);
        var (fields, version, decoded, end) = BrowserWebSocketProtocolTests.ReadSnapshot(buffer.WrittenMemory);
        await Assert.That(fields).IsEqualTo(35);
        await Assert.That(version).IsEqualTo(9);
        await Assert.That(end).IsTrue();
        await Assert.That(decoded.CanRematch).IsEqualTo(expected);
    }

    private static StatePacket AuthoritativeState(Guid session, PaddleSide hostSide, GameState state) => new()
    {
        SessionId = session, Sequence = state.TickNumber, RoundId = state.RoundId, HostSide = hostSide,
        LeftY = state.LeftY, RightY = state.RightY, BallX = state.BallX, BallY = state.BallY,
        BallVx = state.BallVx, BallVy = state.BallVy, LeftScore = state.LeftScore, RightScore = state.RightScore,
        Phase = state.Phase, Countdown = state.Countdown, ServeDirection = state.ServeDirection,
        Hits = state.Hits, HostAxis = 0, RecentEvents = state.RecentEvents.ToArray(),
        LastEventTick = state.LastEventTick, EventOrdinal = state.EventOrdinal
    };

    private static PongPeer Peer(Func<PaddleSide>? resolver = null) => new(NullLogger<PongPeer>.Instance,
        BotTestSupport.Runtime([BotTestSupport.Tracker()], new TrackerBotStrategyFactory()), resolver);

    private static TestBotFactory Factory(Func<BotDefinition, ILocalOpponentController> create) =>
        new(BotStrategyDescriptor.TrackerId, BotSettingsKind.Tracker, create);
}
