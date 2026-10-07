using System.Buffers;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanPong.Tests;

public sealed class LocalOpponentSessionTests
{
    [Test]
    public async Task StartLocalOpponentAsync_AdvancesBothAssignedPaddlesWithoutUdpAndSupportsRematchAndLeave()
    {
        var opponent = new TestOpponent();
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, opponent);

        await peer.StartLocalOpponentAsync("Игрок");
        var started = peer.Snapshot();
        await Assert.That(started.Role).IsEqualTo(PeerRole.Host);
        await Assert.That(started.OpponentMode).IsEqualTo(OpponentMode.Simple);
        await Assert.That(started.Connection).IsEqualTo(ConnectionState.Connected);
        await Assert.That(started.Phase).IsEqualTo(GamePhase.Countdown);
        await Assert.That(started.UdpPort).IsEqualTo(0);
        await Assert.That(started.PeerAddress).IsNull();
        await Assert.That(started.PingMs).IsNull();
        await Assert.That(started.PeerNickname).IsEqualTo("Компьютер");
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
    public async Task StartLocalOpponentAsync_UsesVersionedBrowserSnapshotAndCanReturnToLanHosting()
    {
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, new TestOpponent());
        await peer.StartLocalOpponentAsync("Игрок");
        var local = peer.Snapshot();
        var buffer = new ArrayBufferWriter<byte>();
        BrowserWebSocketProtocol.WriteSnapshot(local, buffer);
        var (fieldCount, version, role, connection, udpPort, noPeerAddress, mode, atEnd) =
            ReadBrowserHeader(buffer.WrittenMemory);

        await Assert.That(fieldCount).IsEqualTo(24);
        await Assert.That(version).IsEqualTo(6);
        await Assert.That(role).IsEqualTo((int)PeerRole.Host);
        await Assert.That(connection).IsEqualTo((int)ConnectionState.Connected);
        await Assert.That(udpPort).IsEqualTo(0);
        await Assert.That(noPeerAddress).IsTrue();
        await Assert.That(mode).IsEqualTo((int)OpponentMode.Simple);
        await Assert.That(atEnd).IsTrue();

        await peer.LeaveAsync();
        await Assert.That(peer.Snapshot().OpponentMode).IsEqualTo(OpponentMode.None);
        int port;
        using (var reserved = new System.Net.Sockets.UdpClient(0))
            port = ((System.Net.IPEndPoint)reserved.Client.LocalEndPoint!).Port;
        await peer.HostAsync(port, "Игрок");
        var lobby = peer.Snapshot();
        await Assert.That(lobby.Role).IsEqualTo(PeerRole.Host);
        await Assert.That(lobby.OpponentMode).IsEqualTo(OpponentMode.Lan);
        await Assert.That(lobby.Connection).IsEqualTo(ConnectionState.Waiting);
        await Assert.That(lobby.UdpPort).IsEqualTo(port);
        await Assert.That(lobby.Phase).IsEqualTo(GamePhase.Waiting);
        await peer.LeaveAsync();
    }

    private static (int FieldCount, int Version, int Role, int Connection, int UdpPort,
        bool NoPeerAddress, int Mode, bool AtEnd) ReadBrowserHeader(ReadOnlyMemory<byte> bytes)
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
        return (fieldCount, version, role, connection, udpPort, noPeerAddress, mode, reader.End);
    }

    private static async Task<PongSnapshot> WaitForAsync(PongPeer peer, Func<PongSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = peer.Snapshot();
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(10);
        }
        throw new TimeoutException("Local opponent session did not advance both paddles.");
    }

    private sealed class TestOpponent : ILocalOpponentController
    {
        public int ResetCount { get; private set; }

        public void Reset() => ResetCount++;

        public int GetAxis(GameState state) => -1;
    }
}
