using System.Buffers;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanPong.Tests;

public sealed class LocalOpponentSessionTests
{
    [Test]
    public async Task StartBotAsync_AdvancesBothAssignedPaddlesWithoutUdpAndSupportsRematchAndLeave()
    {
        var opponent = new TrackedBotController();
        var factory = TrackerFactory(_ => opponent);
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([BotTestSupport.Tracker()], factory));

        await peer.StartBotAsync("Игрок", "tracker");
        var started = peer.Snapshot();
        await Assert.That(started.Role).IsEqualTo(PeerRole.Host);
        await Assert.That(started.OpponentMode).IsEqualTo(OpponentMode.Bot);
        await Assert.That(started.RequestedBotId).IsEqualTo("tracker");
        await Assert.That(started.OpponentFallbackActive).IsFalse();
        await Assert.That(started.Version).IsEqualTo(8);
        await Assert.That(started.Connection).IsEqualTo(ConnectionState.Connected);
        await Assert.That(started.Phase).IsEqualTo(GamePhase.Countdown);
        await Assert.That(started.UdpPort).IsEqualTo(0);
        await Assert.That(started.PeerAddress).IsNull();
        await Assert.That(started.PingMs).IsNull();
        await Assert.That(started.PeerNickname).IsNull();
        await Assert.That(started.RequestedBotName).IsEqualTo("tracker");
        await Assert.That(started.EffectiveBotId).IsEqualTo("tracker");
        await Assert.That(started.EffectiveBotName).IsEqualTo("tracker");
        await Assert.That(started.BotFallbackReason).IsNull();
        await Assert.That(started.RecentEvents.Single().Kind).IsEqualTo(GameEventKind.MatchStart);

        var browserController = Guid.NewGuid();
        peer.SetInput(browserController, 1);
        var moved = await WaitForAsync(peer, snapshot =>
            snapshot.Tick > started.Tick && snapshot.LeftY > 0.52 && snapshot.RightY < 0.48);
        await Assert.That(moved.LeftY).IsGreaterThan(started.LeftY);
        await Assert.That(moved.RightY).IsLessThan(started.RightY);

        peer.Restart();
        var restarted = peer.Snapshot();
        await Assert.That(restarted.RoundId).IsEqualTo(started.RoundId + 1);
        await Assert.That(restarted.LeftY).IsEqualTo(GameConstants.ArenaCenter);
        await Assert.That(restarted.RightY).IsEqualTo(GameConstants.ArenaCenter);
        await Assert.That(restarted.LeftScore).IsEqualTo(0);
        await Assert.That(restarted.RightScore).IsEqualTo(0);
        await Assert.That(opponent.ResetCount).IsEqualTo(2);

        await peer.LeaveAsync();
        var left = peer.Snapshot();
        await Assert.That(left.Role).IsEqualTo(PeerRole.None);
        await Assert.That(left.Connection).IsEqualTo(ConnectionState.Idle);
        await Assert.That(left.Phase).IsEqualTo(GamePhase.Waiting);
        await Assert.That(left.Tick).IsEqualTo(0);
        await Task.Delay(50);
        await Assert.That(peer.Snapshot().Tick).IsEqualTo(0);
    }

    [Test]
    public async Task StartBotAsync_UsesVersionedBrowserSnapshotAndCanReturnToLanHosting()
    {
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([BotTestSupport.Tracker()], TrackerFactory(_ => new TrackedBotController())));
        await peer.StartBotAsync("Игрок", "tracker");
        var local = peer.Snapshot();
        var buffer = new ArrayBufferWriter<byte>();
        BrowserWebSocketProtocol.WriteSnapshot(local, buffer);
        var (fieldCount, version, role, connection, udpPort, noPeerAddress, mode,
            requestedId, effectiveId, fallback, reason, atEnd) =
            ReadBrowserHeader(buffer.WrittenMemory);

        await Assert.That(fieldCount).IsEqualTo(30);
        await Assert.That(version).IsEqualTo(8);
        await Assert.That(role).IsEqualTo((int)PeerRole.Host);
        await Assert.That(connection).IsEqualTo((int)ConnectionState.Connected);
        await Assert.That(udpPort).IsEqualTo(0);
        await Assert.That(noPeerAddress).IsTrue();
        await Assert.That(mode).IsEqualTo((int)OpponentMode.Bot);
        await Assert.That(requestedId).IsEqualTo("tracker");
        await Assert.That(effectiveId).IsEqualTo("tracker");
        await Assert.That(reason).IsNull();
        await Assert.That(fallback).IsFalse();
        await Assert.That(atEnd).IsTrue();

        await peer.LeaveAsync();
        await Assert.That(peer.Snapshot().OpponentMode).IsEqualTo(OpponentMode.None);
        await Assert.That(peer.Snapshot().RequestedBotId).IsNull();
        await Assert.That(peer.Snapshot().EffectiveBotId).IsNull();
        int port;
        using (var reserved = new System.Net.Sockets.UdpClient(0))
            port = ((System.Net.IPEndPoint)reserved.Client.LocalEndPoint!).Port;
        await peer.HostAsync(port, "Игрок");
        var lobby = peer.Snapshot();
        await Assert.That(lobby.Role).IsEqualTo(PeerRole.Host);
        await Assert.That(lobby.OpponentMode).IsEqualTo(OpponentMode.Lan);
        await Assert.That(lobby.RequestedBotId).IsNull();
        await Assert.That(lobby.EffectiveBotId).IsNull();
        await Assert.That(lobby.OpponentFallbackActive).IsFalse();
        await Assert.That(lobby.Connection).IsEqualTo(ConnectionState.Waiting);
        await Assert.That(lobby.UdpPort).IsEqualTo(port);
        await Assert.That(lobby.Phase).IsEqualTo(GamePhase.Waiting);
        await peer.LeaveAsync();
    }

    [Test]
    public async Task StartModelBotAsync_WhenModelIsMissing_ReportsFallbackAndKeepsSessionPlayable()
    {
        var missingModel = Path.Combine(Path.GetTempPath(), $"missing-lanpong-{Guid.NewGuid():N}.onnx");
        var runtime = BotTestSupport.Runtime([BotTestSupport.Tracker(name: "Tracker"),
                BotTestSupport.Onnx(fallback: "tracker", path: missingModel)],
            new TrackerBotStrategyFactory(), OnnxFactory(entry => new OnnxLocalOpponentController(entry.Onnx!)));
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);

        await Assert.That(peer.BotStatus).IsNull();
        await peer.StartBotAsync("Игрок", "model");
        var started = peer.Snapshot();
        var status = peer.BotStatus;
        await Assert.That(status!.RequestedBotId).IsEqualTo("model");
        await Assert.That(status.EffectiveBotId).IsEqualTo("tracker");
        await Assert.That(string.IsNullOrWhiteSpace(status.FallbackReason)).IsFalse();
        await Assert.That(started.OpponentMode).IsEqualTo(OpponentMode.Bot);
        await Assert.That(started.RequestedBotId).IsEqualTo("model");
        await Assert.That(started.OpponentFallbackActive).IsTrue();
        await Assert.That(started.Connection).IsEqualTo(ConnectionState.Connected);
        await Assert.That(started.UdpPort).IsEqualTo(0);
        await Assert.That(started.Message.Contains("Tracker", StringComparison.Ordinal)).IsTrue();
        await Assert.That(started.Message.Contains("FileNotFoundException", StringComparison.Ordinal))
            .IsFalse();

        var playing = await WaitForAsync(peer, snapshot => snapshot.Tick > started.Tick);
        await Assert.That(playing.Tick).IsGreaterThan(started.Tick);
        peer.Restart();
        await Assert.That(peer.Snapshot().RoundId).IsEqualTo(started.RoundId + 1);
        await Assert.That(peer.BotStatus!.EffectiveBotId).IsEqualTo("tracker");
        await Assert.That(peer.Snapshot().RequestedBotId).IsEqualTo("model");
        await Assert.That(peer.Snapshot().OpponentFallbackActive).IsTrue();
        await Assert.That(peer.Snapshot().OpponentMode).IsEqualTo(OpponentMode.Bot);
        await Assert.That(peer.Snapshot().Message.Contains("Tracker", StringComparison.Ordinal)).IsTrue();

        await peer.LeaveAsync();
        await Assert.That(peer.BotStatus).IsNull();
        await Assert.That(peer.Snapshot().BotFallbackReason).IsNull();
        await Assert.That(peer.Snapshot().OpponentMode).IsEqualTo(OpponentMode.None);
        await Assert.That(peer.Snapshot().RequestedBotId).IsNull();
        await Assert.That(peer.Snapshot().EffectiveBotId).IsNull();
        await Assert.That(peer.Snapshot().OpponentFallbackActive).IsFalse();

        await peer.StartBotAsync("Игрок", "tracker");
        await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("tracker");
        await Assert.That(peer.Snapshot().RequestedBotId).IsEqualTo("tracker");
        await Assert.That(peer.Snapshot().EffectiveBotName).IsEqualTo("Tracker");
        await Assert.That(peer.Snapshot().BotFallbackReason).IsNull();
        await peer.LeaveAsync();
    }

    [Test]
    public async Task StartModelBotAsync_WithFrozenModel_PreservesSelectedIdentityAcrossRematch()
    {
        OnnxLocalOpponentController? modelController = null;
        var runtime = BotTestSupport.Runtime([BotTestSupport.Tracker(),
                BotTestSupport.Onnx(path: BotTestSupport.ModelPath())],
            new TrackerBotStrategyFactory(),
            OnnxFactory(entry => modelController = new OnnxLocalOpponentController(entry.Onnx!)));
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);

        await peer.StartBotAsync("Игрок", "model");
        var started = peer.Snapshot();
        await Assert.That(started.OpponentMode).IsEqualTo(OpponentMode.Bot);
        await Assert.That(started.RequestedBotId).IsEqualTo("model");
        await Assert.That(started.OpponentFallbackActive).IsFalse();
        await Assert.That(modelController!.ModelSha256).IsEqualTo(BotModelV1.ExpectedSha256);
        await WaitForAsync(peer, snapshot => snapshot.Tick > started.Tick);

        peer.Restart();
        var restarted = peer.Snapshot();
        await Assert.That(restarted.RoundId).IsEqualTo(started.RoundId + 1);
        await Assert.That(restarted.OpponentMode).IsEqualTo(OpponentMode.Bot);
        await Assert.That(restarted.RequestedBotId).IsEqualTo("model");
        await Assert.That(restarted.EffectiveBotId).IsEqualTo("model");
        await Assert.That(restarted.OpponentFallbackActive).IsFalse();

        await peer.LeaveAsync();
        var left = peer.Snapshot();
        await Assert.That(left.OpponentMode).IsEqualTo(OpponentMode.None);
        await Assert.That(left.RequestedBotId).IsNull();
        await Assert.That(left.EffectiveBotId).IsNull();
        await Assert.That(left.BotFallbackReason).IsNull();
        await Assert.That(left.OpponentFallbackActive).IsFalse();
    }

    [Test]
    public async Task JoinAsync_ImmediateLeaveDoesNotRaceReceiverStartup()
    {
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([BotTestSupport.Tracker()], new TrackerBotStrategyFactory()));

        for (var attempt = 0; attempt < 32; attempt++)
        {
            await peer.JoinAsync("127.0.0.1", 9, "Игрок");
            await peer.LeaveAsync();
            var snapshot = peer.Snapshot();
            await Assert.That(snapshot.Connection).IsEqualTo(ConnectionState.Idle);
            await Assert.That(snapshot.OpponentMode).IsEqualTo(OpponentMode.None);
        }
    }

    [Test]
    public async Task StartModelBotAsync_WhenInferenceFails_UpdatesStatusWithoutStoppingClock()
    {
        var inference = new ThrowingInferenceSession();
        var runtime = BotTestSupport.Runtime([BotTestSupport.Tracker(name: "Tracker"),
                BotTestSupport.Onnx(fallback: "tracker")],
            new TrackerBotStrategyFactory(),
            OnnxFactory(entry => new OnnxLocalOpponentController(entry.Onnx!, inference)));
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);

        await peer.StartBotAsync("Игрок", "model");
        await Assert.That(peer.BotStatus!.RequestedBotId).IsEqualTo("model");
        await Assert.That(peer.BotStatus.EffectiveBotId).IsEqualTo("model");
        await Assert.That(peer.BotStatus.FallbackReason).IsNull();
        await Assert.That(peer.Snapshot().OpponentMode).IsEqualTo(OpponentMode.Bot);
        await Assert.That(peer.Snapshot().RequestedBotId).IsEqualTo("model");
        await Assert.That(peer.Snapshot().OpponentFallbackActive).IsFalse();

        var fallback = await WaitForAsync(peer, _ => peer.BotStatus?.EffectiveBotId == "tracker" && inference.Disposed,
            TimeSpan.FromSeconds(5));
        await Assert.That(fallback.Message.Contains("Tracker", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fallback.OpponentMode).IsEqualTo(OpponentMode.Bot);
        await Assert.That(fallback.RequestedBotId).IsEqualTo("model");
        await Assert.That(fallback.EffectiveBotId).IsEqualTo("tracker");
        await Assert.That(fallback.OpponentFallbackActive).IsTrue();
        await Assert.That(peer.BotStatus!.FallbackReason).IsNotNull();
        await Assert.That(inference.Disposed).IsTrue();

        var advanced = await WaitForAsync(peer, snapshot => snapshot.Tick > fallback.Tick);
        await Assert.That(advanced.Tick).IsGreaterThan(fallback.Tick);
        await peer.LeaveAsync();
    }

    private static (int FieldCount, int Version, int Role, int Connection, int UdpPort,
        bool NoPeerAddress, int Mode, string? RequestedId, string? EffectiveId, bool Fallback, string? Reason, bool AtEnd)
        ReadBrowserHeader(ReadOnlyMemory<byte> bytes)
    {
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
        var fieldCount = reader.ReadArrayHeader();
        var version = reader.ReadInt32();
        var role = reader.ReadInt32();
        var connection = reader.ReadInt32();
        reader.Skip(); // Message.
        var udpPort = reader.ReadInt32();
        reader.Skip(); // Local addresses.
        var noPeerAddress = reader.TryReadNil();
        for (var field = 7; field < 23; field++) reader.Skip();
        var mode = reader.ReadInt32();
        var requestedId = reader.ReadString();
        reader.Skip(); // Requested name.
        var effectiveId = reader.ReadString();
        reader.Skip(); // Effective name.
        var fallback = reader.ReadBoolean();
        var reason = reader.ReadString();
        return (fieldCount, version, role, connection, udpPort, noPeerAddress,
            mode, requestedId, effectiveId, fallback, reason, reader.End);
    }

    private static async Task<PongSnapshot> WaitForAsync(PongPeer peer, Func<PongSnapshot, bool> predicate,
        TimeSpan? timeoutAfter = null)
    {
        using var timeout = new CancellationTokenSource(timeoutAfter ?? TimeSpan.FromSeconds(2));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = peer.Snapshot();
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(10);
        }
        throw new TimeoutException("Local opponent session did not advance both paddles.");
    }

    private static TestBotFactory TrackerFactory(Func<BotDefinition, ILocalOpponentController> create) =>
        new(BotStrategyDescriptor.TrackerId, BotSettingsKind.Tracker, create);

    private static TestBotFactory OnnxFactory(Func<BotDefinition, ILocalOpponentController> create) =>
        new(BotStrategyDescriptor.OnnxId, BotSettingsKind.Onnx, create);

    private sealed class ThrowingInferenceSession : IOnnxInferenceSession
    {
        public bool Disposed { get; private set; }

        public void Run(ReadOnlySpan<float> observation, Span<float> logits) =>
            throw new InvalidDataException("Synthetic inference failure.");

        public void Dispose() => Disposed = true;
    }
}
