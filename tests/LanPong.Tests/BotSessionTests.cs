using LanPong.Bots.Catalog;
using LanPong.Bots.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanPong.Tests;

public sealed class BotSessionTests
{
    [Test]
    public async Task SharedStrategy_SelectsConfiguredIdsAndPassesEachEntriesOwnSettings()
    {
        var controllers = new Dictionary<string, TrackedBotController>();
        var factory = TrackerFactory(entry => controllers[entry.Id] =
            new TrackedBotController(entry.Tracker!.TargetDeadZone < 0.1 ? -1 : 1));
        var runtime = BotTestSupport.Runtime([
            BotTestSupport.Tracker("calm", "Calm profile", cadence: 13, deadZone: 0.02),
            BotTestSupport.Tracker("bold", "Bold profile", cadence: 4, deadZone: 0.12)
        ], factory);
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);

        await peer.StartBotAsync("Player", "calm", InitialSidePreference.Left);
        await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("calm");
        await Assert.That(peer.BotStatus.RequestedName).IsEqualTo("Calm profile");
        await Assert.That(peer.BotStatus.EffectiveBotId).IsEqualTo("calm");
        await Assert.That(peer.BotStatus.FallbackReason).IsNull();
        await BotTestSupport.WaitForAsync(peer, snapshot => snapshot.RightY < 0.49);

        await peer.LeaveAsync();
        await peer.StartBotAsync("Another player", "bold", InitialSidePreference.Left);
        await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("bold");
        await Assert.That(peer.BotStatus.EffectiveName).IsEqualTo("Bold profile");
        await BotTestSupport.WaitForAsync(peer, snapshot => snapshot.RightY > 0.51);
        var definitions = factory.Definitions.ToArray();
        await Assert.That(definitions.Length).IsEqualTo(2);
        await Assert.That(definitions[0].Tracker!.ObservationIntervalTicks).IsEqualTo(13);
        await Assert.That(definitions[0].Tracker!.TargetDeadZone).IsEqualTo(0.02);
        await Assert.That(definitions[1].Tracker!.ObservationIntervalTicks).IsEqualTo(4);
        await Assert.That(definitions[1].Tracker!.TargetDeadZone).IsEqualTo(0.12);
        await Assert.That(ReferenceEquals(controllers["calm"], controllers["bold"])).IsFalse();
        await Assert.That(controllers["calm"].DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task RejectedSelection_PreservesCurrentIdentityNicknameRoundAndControllerState()
    {
        var current = new TrackedBotController();
        var unusable = new TrackedBotController { FailReset = _ => true };
        var factory = TrackerFactory(entry => entry.Id == "unusable" ? unusable : current);
        var runtime = BotTestSupport.Runtime([
            BotTestSupport.Tracker("current"), BotTestSupport.Tracker("disabled", enabled: false),
            BotTestSupport.Tracker("unusable")
        ], factory);
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);
        await peer.StartBotAsync("Original player", "current", InitialSidePreference.Left);
        var started = peer.Snapshot();
        var selected = peer.BotStatus;

        foreach (var rejected in new[] { "unknown", "CURRENT", "disabled", "unusable" })
        {
            var error = await BotTestSupport.CaptureAsync(() => peer.StartBotAsync("Changed player", rejected, InitialSidePreference.Left));
            await Assert.That(error).IsNotNull();
            var snapshot = peer.Snapshot();
            await Assert.That(snapshot.Connection).IsEqualTo(ConnectionState.Connected);
            await Assert.That(snapshot.RoundId).IsEqualTo(started.RoundId);
            await Assert.That(snapshot.LocalNickname).IsEqualTo("Original player");
            await Assert.That(peer.BotStatus).IsEqualTo(selected);
            await Assert.That(current.ResetCount).IsEqualTo(1);
            await Assert.That(current.DisposeCount).IsEqualTo(0);
        }
        await Assert.That(unusable.DisposeCount).IsEqualTo(0);
        await Assert.That(factory.Definitions.Count).IsEqualTo(1);
        var advanced = await BotTestSupport.WaitForAsync(peer, snapshot => snapshot.Tick > started.Tick);
        await Assert.That(advanced.Role).IsEqualTo(PeerRole.Host);

        await peer.LeaveAsync();
        foreach (var rejected in new[] { "unknown", "CURRENT", "disabled", "unusable" })
        {
            var error = await BotTestSupport.CaptureAsync(() => peer.StartBotAsync("Changed player", rejected, InitialSidePreference.Left));
            await Assert.That(error).IsNotNull();
            var snapshot = peer.Snapshot();
            await Assert.That(snapshot.Connection).IsEqualTo(ConnectionState.Idle);
            await Assert.That(snapshot.Role).IsEqualTo(PeerRole.None);
            await Assert.That(snapshot.Phase).IsEqualTo(GamePhase.Waiting);
            await Assert.That(snapshot.LocalNickname).IsEqualTo("Original player");
            await Assert.That(peer.BotStatus).IsNull();
        }
        await Assert.That(unusable.DisposeCount).IsEqualTo(1);
        await Assert.That(factory.Definitions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task LeaveNewSelectionRematchAndShutdown_KeepWarmControllerThenDisposeExactlyOnce()
    {
        var first = new TrackedBotController();
        var second = new TrackedBotController();
        var factory = TrackerFactory(entry => entry.Id == "first" ? first : second);
        var runtime = BotTestSupport.Runtime([
            BotTestSupport.Tracker("first"), BotTestSupport.Tracker("second")
        ], factory);
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);
        await peer.StartBotAsync("Player", "first", InitialSidePreference.Left);
        await peer.LeaveAsync();
        await peer.StartBotAsync("Player", "second", InitialSidePreference.Left);
        var started = peer.Snapshot();

        await Assert.That(first.DisposeCount).IsEqualTo(1);
        await Assert.That(second.DisposeCount).IsEqualTo(0);
        await PeerTestAccess.FinishAsync(peer);
        peer.Restart(Guid.ParseExact(peer.Snapshot().MatchId!, "N"), peer.Snapshot().RoundId);
        await Assert.That(peer.Snapshot().RoundId).IsEqualTo(started.RoundId + 1);
        await Assert.That(second.ResetCount).IsEqualTo(2);
        await Assert.That(factory.Definitions.Count).IsEqualTo(2);
        await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("second");
        await peer.StopAsync(CancellationToken.None);
        await peer.StopAsync(CancellationToken.None);
        await Assert.That(first.DisposeCount).IsEqualTo(1);
        await Assert.That(second.DisposeCount).IsEqualTo(1);
        await Assert.That(peer.BotStatus).IsNull();
    }

    [Test]
    public async Task LeaveAndDirectLanTransition_ClearIdentityAndDisposeOwnedController()
    {
        var created = new List<TrackedBotController>();
        var factory = TrackerFactory(_ =>
        {
            var controller = new TrackedBotController();
            created.Add(controller);
            return controller;
        });
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([BotTestSupport.Tracker()], factory));
        await peer.StartBotAsync("Player", "tracker", InitialSidePreference.Left);
        await peer.LeaveAsync();
        await Assert.That(peer.BotStatus).IsNull();
        await Assert.That(created[0].DisposeCount).IsEqualTo(1);
        await Assert.That(peer.Snapshot().OpponentMode).IsEqualTo(OpponentMode.None);

        await peer.StartBotAsync("Player", "tracker", InitialSidePreference.Left);
        await peer.HostAsync(BotTestSupport.ReservePort(), "LAN player");
        await Assert.That(peer.BotStatus).IsNull();
        await Assert.That(created[1].DisposeCount).IsEqualTo(1);
        await Assert.That(peer.Snapshot().OpponentMode).IsEqualTo(OpponentMode.Lan);
        await Assert.That(peer.Snapshot().RequestedBotId).IsNull();
        await Assert.That(peer.Snapshot().EffectiveBotId).IsNull();
        await Assert.That(peer.Snapshot().BotFallbackReason).IsNull();
        await Assert.That(peer.Snapshot().PeerNickname).IsNull();
    }

    [Test]
    public async Task PreparationFailure_UsesExplicitFallbackIdentityTuningAndPersistentNotice()
    {
        var failed = new TrackedBotController { FailReset = _ => true };
        var fallback = new TrackedBotController(1);
        var trackers = TrackerFactory(_ => fallback);
        var runtime = BotTestSupport.Runtime([
            BotTestSupport.Tracker("tuned-rescue", "Tuned rescue", cadence: 3, deadZone: 0.07),
            BotTestSupport.Onnx("selected-model", "tuned-rescue")
        ], trackers, OnnxFactory(_ => failed));
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);
        await peer.StartBotAsync("Player", "selected-model", InitialSidePreference.Left);
        var started = peer.Snapshot();

        await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("selected-model");
        await Assert.That(peer.BotStatus.RequestedName).IsEqualTo("Model selected-model");
        await Assert.That(peer.BotStatus.EffectiveBotId).IsEqualTo("tuned-rescue");
        await Assert.That(peer.BotStatus.EffectiveName).IsEqualTo("Tuned rescue");
        await Assert.That(peer.BotStatus.FallbackReason).IsNotNull();
        await Assert.That(peer.BotStatus.FallbackReason!.Contains("/private", StringComparison.Ordinal)).IsFalse();
        await Assert.That(started.Message).Contains("Tuned rescue");
        await Assert.That(started.Message.Contains("InvalidDataException", StringComparison.Ordinal)).IsFalse();
        await Assert.That(trackers.Definitions.Single().Tracker!.ObservationIntervalTicks).IsEqualTo(3);
        await Assert.That(trackers.Definitions.Single().Tracker!.TargetDeadZone).IsEqualTo(0.07);
        await Assert.That(failed.DisposeCount).IsEqualTo(1);
        await BotTestSupport.WaitForAsync(peer, snapshot => snapshot.RightY > 0.51);

        var identity = peer.BotStatus;
        await PeerTestAccess.FinishAsync(peer);
        peer.Restart(Guid.ParseExact(peer.Snapshot().MatchId!, "N"), peer.Snapshot().RoundId);
        await Assert.That(peer.Snapshot().RoundId).IsEqualTo(started.RoundId + 1);
        await Assert.That(peer.BotStatus).IsEqualTo(identity);
        await Assert.That(peer.Snapshot().Message).Contains("Tuned rescue");
        await Assert.That(fallback.ResetCount).IsEqualTo(2);
        await Assert.That(trackers.Definitions.Count).IsEqualTo(1);
        await peer.LeaveAsync();
        await Assert.That(peer.BotStatus).IsNull();
        await Assert.That(peer.Snapshot().BotFallbackReason).IsNull();
        await Assert.That(fallback.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task RuntimeFailure_UsesPreparedOnnxFallbackWithoutCreatingResourcesOnTick()
    {
        var primary = new TrackedBotController { FailAxis = _ => true };
        var fallback = new TrackedBotController(1);
        var models = OnnxFactory(entry => entry.Id == "primary" ? primary : fallback);
        var runtime = BotTestSupport.Runtime([
            BotTestSupport.Onnx("primary", "model-rescue"), BotTestSupport.Onnx("model-rescue")
        ], models);
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);
        await peer.StartBotAsync("Player", "primary", InitialSidePreference.Left);

        await Assert.That(models.Definitions.Count).IsEqualTo(2);
        await Assert.That(fallback.ResetCount).IsGreaterThanOrEqualTo(1);
        await BotTestSupport.WaitForAsync(peer, _ => peer.BotStatus?.EffectiveBotId == "model-rescue" &&
            primary.DisposeCount == 1);
        await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("primary");
        await Assert.That(peer.BotStatus.EffectiveName).IsEqualTo("Model model-rescue");
        await Assert.That(peer.BotStatus.FallbackReason).IsNotNull();
        await Assert.That(models.Definitions.Count).IsEqualTo(2);
        await Assert.That(primary.DisposeCount).IsEqualTo(1);
        await Assert.That(fallback.DisposeCount).IsEqualTo(0);
        await PeerTestAccess.FinishAsync(peer);
        peer.Restart(Guid.ParseExact(peer.Snapshot().MatchId!, "N"), peer.Snapshot().RoundId);
        await Assert.That(models.Definitions.Count).IsEqualTo(2);
        await Assert.That(peer.BotStatus.EffectiveBotId).IsEqualTo("model-rescue");
        await peer.LeaveAsync();
        await Assert.That(primary.DisposeCount).IsEqualTo(1);
        await Assert.That(fallback.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task FallbackWarmup_KeepsSnapshotResponsiveAndSessionIdleUntilAllBackupsPrepared()
    {
        var preparingFallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new TrackedBotController { FailAxis = _ => true };
        var fallback = new TrackedBotController
        {
            OnReset = count =>
            {
                if (count != 1) return;
                preparingFallback.TrySetResult();
                if (!release.Task.Wait(BarrierReleaseTimeout))
                    throw new TimeoutException("Fallback preparation barrier timed out.");
            }
        };
        var models = OnnxFactory(entry => entry.Id == "selected" ? selected : fallback);
        var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([
                BotTestSupport.Onnx("selected", "model-rescue"), BotTestSupport.Onnx("model-rescue")
            ], models));
        var idle = peer.Snapshot();
        Task? preparation = null;
        Task<PongSnapshot>? snapshotProbe = null;
        Exception? primaryFailure = null;
        try
        {
            preparation = peer.StartBotAsync("Selected player", "selected", InitialSidePreference.Left);
            await preparingFallback.Task.WaitAsync(TimeSpan.FromSeconds(3));
            snapshotProbe = SnapshotOnDedicatedThread(peer);
            var snapshot = await snapshotProbe.WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.That(snapshot.LocalNickname).IsEqualTo(idle.LocalNickname);
            await Assert.That(peer.BotStatus).IsNull();
            await Assert.That(snapshot.Role).IsEqualTo(PeerRole.None);
            await Assert.That(snapshot.Connection).IsEqualTo(ConnectionState.Idle);
            await Assert.That(snapshot.Phase).IsEqualTo(GamePhase.Waiting);
            await Assert.That(snapshot.Tick).IsEqualTo(0);
            await Assert.That(selected.DisposeCount).IsEqualTo(0);
            release.TrySetResult();
            await preparation.WaitAsync(TimeSpan.FromSeconds(3));
            await BotTestSupport.WaitForAsync(peer, _ => peer.BotStatus?.EffectiveBotId == "model-rescue" &&
                selected.DisposeCount == 1);
            await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("selected");
            await Assert.That(peer.Snapshot().LocalNickname).IsEqualTo("Selected player");
            await Assert.That(models.Definitions.Count).IsEqualTo(2);
            await Assert.That(fallback.ResetCount).IsEqualTo(1);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally { await ReleaseAndObserveAsync(release, peer, primaryFailure, preparation, snapshotProbe); }
    }

    [Test]
    public async Task RuntimeFailureWithoutFallback_StopsCleanlyAndSharedClockStillAdvancesLanMatch()
    {
        var failed = new TrackedBotController { FailAxis = _ => true };
        await using var host = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([BotTestSupport.Tracker("failing")], TrackerFactory(_ => failed)));
        await host.StartBotAsync("Player", "failing", InitialSidePreference.Left);
        var stopped = await BotTestSupport.WaitForAsync(host, snapshot => snapshot.Connection == ConnectionState.Idle &&
            failed.DisposeCount == 1);
        await Assert.That(stopped.Role).IsEqualTo(PeerRole.None);
        await Assert.That(stopped.Phase).IsEqualTo(GamePhase.Waiting);
        await Assert.That(stopped.Tick).IsEqualTo(0);
        await Assert.That(host.BotStatus).IsNull();
        await Assert.That(stopped.Message.Contains("/private", StringComparison.Ordinal)).IsFalse();
        await Assert.That(stopped.Message.Contains("InvalidDataException", StringComparison.Ordinal)).IsFalse();
        await Assert.That(failed.DisposeCount).IsEqualTo(1);

        await using var guest = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([BotTestSupport.Tracker()], new TrackerBotStrategyFactory()));
        await host.HostAsync(BotTestSupport.ReservePort(), "LAN host");
        await guest.JoinAsync("127.0.0.1", host.Snapshot().UdpPort, "LAN guest");
        await BotTestSupport.WaitForAsync(host, snapshot => snapshot.Connection == ConnectionState.IncomingChallenge);
        await host.AcceptChallengeAsync();
        await BotTestSupport.WaitForAsync(guest, snapshot => snapshot.Connection == ConnectionState.Connected);
        var playing = await BotTestSupport.WaitForAsync(host, snapshot => snapshot.Tick > 2);
        await Assert.That(playing.OpponentMode).IsEqualTo(OpponentMode.Lan);
        await BotTestSupport.WaitForAsync(guest, snapshot => snapshot.Tick > 0);
    }

    [Test]
    public async Task RematchResetFailure_FollowsConfiguredChainWithoutRecreatingControllers()
    {
        var primary = new TrackedBotController { FailReset = count => count > 1 };
        var fallback = new TrackedBotController();
        var factory = TrackerFactory(entry => entry.Id == "primary" ? primary : fallback);
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([
                BotTestSupport.Tracker("primary", fallback: "rescue"), BotTestSupport.Tracker("rescue")
            ], factory));
        await peer.StartBotAsync("Player", "primary", InitialSidePreference.Left);
        var started = peer.Snapshot();

        await PeerTestAccess.FinishAsync(peer);
        peer.Restart(Guid.ParseExact(peer.Snapshot().MatchId!, "N"), peer.Snapshot().RoundId);
        await Assert.That(peer.Snapshot().RoundId).IsEqualTo(started.RoundId + 1);
        await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("primary");
        await Assert.That(peer.BotStatus.EffectiveBotId).IsEqualTo("rescue");
        await Assert.That(peer.BotStatus.FallbackReason).IsNotNull();
        await Assert.That(primary.DisposeCount).IsEqualTo(1);
        await Assert.That(factory.Definitions.Count).IsEqualTo(2);
        await BotTestSupport.WaitForAsync(peer, snapshot => snapshot.Tick > 0);
    }

    [Test]
    public async Task RematchResetFailureWithoutFallback_ReturnsToCoherentIdleState()
    {
        var primary = new TrackedBotController { FailReset = count => count > 1 };
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([BotTestSupport.Tracker()], TrackerFactory(_ => primary)));
        await peer.StartBotAsync("Player", "tracker", InitialSidePreference.Left);

        var finished = await PeerTestAccess.FinishAsync(peer);
        await Assert.That(() => peer.Restart(Guid.ParseExact(finished.MatchId!, "N"), finished.RoundId)).Throws<InvalidOperationException>();
        var stopped = peer.Snapshot();
        await Assert.That(stopped.Role).IsEqualTo(PeerRole.None);
        await Assert.That(stopped.Connection).IsEqualTo(ConnectionState.Idle);
        await Assert.That(stopped.Phase).IsEqualTo(GamePhase.Waiting);
        await Assert.That(stopped.RoundId).IsEqualTo(finished.RoundId);
        await Assert.That(stopped.LocalSide).IsNull();
        await Assert.That(stopped.MatchId).IsNull();
        await Assert.That(peer.BotStatus).IsNull();
        await Assert.That(primary.DisposeCount).IsEqualTo(1);
        await peer.HostAsync(BotTestSupport.ReservePort(), "LAN host");
        await Assert.That(peer.Snapshot().Connection).IsEqualTo(ConnectionState.Waiting);
    }

    [Test]
    public async Task FailedControllerDisposal_RunsOutsideStateLockAndCannotFaultSharedClock()
    {
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new TrackedBotController
        {
            FailAxis = _ => true,
            OnDispose = () =>
            {
                disposing.TrySetResult();
                if (!release.Task.Wait(BarrierReleaseTimeout)) throw new TimeoutException("Disposal barrier timed out.");
                throw new InvalidOperationException("Synthetic disposal failure.");
            }
        };
        var fallback = new TrackedBotController();
        var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([
                BotTestSupport.Tracker("primary", fallback: "rescue"), BotTestSupport.Tracker("rescue")
            ], TrackerFactory(entry => entry.Id == "primary" ? primary : fallback)));
        Task<PongSnapshot>? snapshotProbe = null;
        Exception? primaryFailure = null;
        try
        {
            await peer.StartBotAsync("Player", "primary", InitialSidePreference.Left);
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            snapshotProbe = SnapshotOnDedicatedThread(peer);
            var snapshot = await snapshotProbe.WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.That(snapshot.Connection).IsEqualTo(ConnectionState.Connected);
            await Assert.That(peer.BotStatus!.EffectiveBotId).IsEqualTo("rescue");
            release.TrySetResult();
            await BotTestSupport.WaitForAsync(peer, current => current.Tick > snapshot.Tick);
            await peer.LeaveAsync();
            await Assert.That(primary.DisposeCount).IsEqualTo(1);
            await Assert.That(fallback.DisposeCount).IsEqualTo(1);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally { await ReleaseAndObserveAsync(release, peer, primaryFailure, snapshotProbe); }
    }

    [Test]
    public async Task ConcurrentStarts_AdmitOnlyFirstPreparedControllerAndRejectQueuedStart()
    {
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidate = new TrackedBotController();
        var factory = TrackerFactory(_ =>
        {
            preparing.TrySetResult();
            if (!release.Task.Wait(BarrierReleaseTimeout))
                throw new TimeoutException("Concurrent preparation barrier timed out.");
            return candidate;
        });
        var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([BotTestSupport.Tracker()], factory));
        Task? first = null;
        Task<Exception?>? second = null;
        Exception? primaryFailure = null;
        try
        {
            first = peer.StartBotAsync("First player", "tracker", InitialSidePreference.Left);
            await preparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            second = BotTestSupport.CaptureAsync(() => peer.StartBotAsync("Second player", "tracker", InitialSidePreference.Left));
            await Assert.That(second.IsCompleted).IsFalse();
            release.TrySetResult();

            await first.WaitAsync(TimeSpan.FromSeconds(3));
            var rejected = await second.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.That(rejected is InvalidOperationException).IsTrue();
            await Assert.That(factory.Definitions.Count).IsEqualTo(1);
            await Assert.That(candidate.ResetCount).IsEqualTo(1);
            await Assert.That(candidate.DisposeCount).IsEqualTo(0);
            await Assert.That(peer.Snapshot().LocalNickname).IsEqualTo("First player");
            await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("tracker");
            await peer.LeaveAsync();
            await Assert.That(candidate.DisposeCount).IsEqualTo(1);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally { await ReleaseAndObserveAsync(release, peer, primaryFailure, first, second); }
    }

    [Test]
    public async Task ShutdownDuringPreparation_RejectsAndDisposesCandidateWithoutHoldingStateLock()
    {
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidate = new TrackedBotController();
        var factory = TrackerFactory(_ =>
        {
            preparing.TrySetResult();
            if (!release.Task.Wait(BarrierReleaseTimeout)) throw new TimeoutException("Preparation barrier timed out.");
            return candidate;
        });
        var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([BotTestSupport.Tracker()], factory));
        Task<Exception?>? start = null;
        Task? stop = null;
        Task<PongSnapshot>? snapshotProbe = null;
        Exception? primaryFailure = null;
        try
        {
            start = BotTestSupport.CaptureAsync(() => peer.StartBotAsync("Player", "tracker", InitialSidePreference.Left));
            await preparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            snapshotProbe = SnapshotOnDedicatedThread(peer);
            var snapshot = await snapshotProbe.WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.That(snapshot.Connection).IsEqualTo(ConnectionState.Idle);
            stop = peer.StopAsync(CancellationToken.None);
            await Assert.That(stop.IsCompleted).IsFalse();
            release.TrySetResult();

            var error = await start.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.That(error is InvalidOperationException).IsTrue();
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.That(candidate.ResetCount).IsEqualTo(1);
            await Assert.That(candidate.DisposeCount).IsEqualTo(1);
            await Assert.That(peer.BotStatus).IsNull();
            await Assert.That(peer.Snapshot().Connection).IsEqualTo(ConnectionState.Idle);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally { await ReleaseAndObserveAsync(release, peer, primaryFailure, start, stop, snapshotProbe); }
    }

    // Task.Wait keeps the callback synchronous while notifying the pool about this intentional hold.
    // Keep it held beyond the entry/probe/assertion budgets; entry still has a three-second deadline.
    private static readonly TimeSpan BarrierReleaseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BarrierCleanupTimeout = TimeSpan.FromSeconds(10);

    private static Task<PongSnapshot> SnapshotOnDedicatedThread(PongPeer peer) =>
        Task.Factory.StartNew(peer.Snapshot, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static async Task ReleaseAndObserveAsync(TaskCompletionSource release, PongPeer peer,
        Exception? primaryFailure, params Task?[] operations)
    {
        release.TrySetResult();
        using var cleanupTimeout = new CancellationTokenSource(BarrierCleanupTimeout);
        List<Exception> cleanupFailures = [];
        for (var index = 0; index < operations.Length; index++)
        {
            var operation = operations[index];
            if (operation is null) continue;
            await ObserveWithinBudgetAsync(operation, $"barrier operation {index + 1}");
        }
        // DisposeAsync enters StopAsync's state lock synchronously, so start even that entry off the observer.
        // Completion also observes the shared clock's deliberately blocked disposal callback.
        try
        {
            var disposal = Task.Factory.StartNew(() => peer.DisposeAsync().AsTask(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            await ObserveWithinBudgetAsync(disposal, "peer disposal");
        }
        catch (Exception error) { cleanupFailures.Add(error); }
        if (cleanupFailures.Count == 0) return;
        var cleanupFailure = new AggregateException("Bot barrier test cleanup failed.", cleanupFailures);
        if (primaryFailure is not null) primaryFailure.Data["BarrierCleanupFailure"] = cleanupFailure;
        else throw cleanupFailure;

        async Task ObserveWithinBudgetAsync(Task operation, string description)
        {
            try { await operation.WaitAsync(cleanupTimeout.Token); }
            catch (OperationCanceledException error) when (cleanupTimeout.IsCancellationRequested)
            {
                // Retain fault observation even if cleanup's shared budget expires before the operation.
                _ = operation.ContinueWith(static completed => { _ = completed.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                var timeout = new TimeoutException($"Cleanup timed out observing {description}.", error);
                timeout.Data["UnfinishedOperation"] = operation;
                cleanupFailures.Add(timeout);
            }
            catch (Exception error) { cleanupFailures.Add(error); }
        }
    }

    private static TestBotFactory TrackerFactory(Func<BotDefinition, ILocalOpponentController> create) =>
        new(BotStrategyDescriptor.TrackerId, BotSettingsKind.Tracker, create);

    private static TestBotFactory OnnxFactory(Func<BotDefinition, ILocalOpponentController> create) =>
        new(BotStrategyDescriptor.OnnxId, BotSettingsKind.Onnx, create);
}
