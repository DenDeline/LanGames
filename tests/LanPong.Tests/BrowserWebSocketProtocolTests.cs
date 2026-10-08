using System.Buffers;
using MessagePack;

namespace LanPong.Tests;

public sealed class BrowserWebSocketProtocolTests
{
    [Test]
    public async Task WriteSnapshot_UsesTheVersionedBrowserArrayLayout()
    {
        var original = new PongSnapshot(
            PeerRole.Host, ConnectionState.Connected, "Соперник подключился.", 47777,
            ["192.168.1.42", "127.0.0.1"], "192.168.1.43:47777",
            0.425, 0.563, 0.712375, 0.2218, 0.4321, -0.348,
            3, 4, GamePhase.Playing, 0.25, 123456, 7, 8.42,
            [new GameEvent("123456:0:2", GameEventKind.Paddle, 123456, 0.712375, 0.2218)],
            "Хозяин", "Гость", OpponentMode.Lan, LocalSide: PaddleSide.Right, MatchId: "ffeeddccbbaa99887766554433221100",
            SourceId: "0123456789abcdef0123456789abcdef", SnapshotSequence: 17);
        var buffer = new ArrayBufferWriter<byte>();

        BrowserWebSocketProtocol.WriteSnapshot(original, buffer);
        var (fieldCount, version, decoded, atEnd) = ReadSnapshot(buffer.WrittenMemory);

        await Assert.That(fieldCount).IsEqualTo(35);
        await Assert.That(version).IsEqualTo(9);
        await Assert.That(atEnd).IsTrue();
        await Assert.That(decoded with
        {
            LocalAddresses = original.LocalAddresses,
            RecentEvents = original.RecentEvents
        }).IsEqualTo(original);
        await Assert.That(decoded.LocalAddresses.SequenceEqual(original.LocalAddresses)).IsTrue();
        await Assert.That(decoded.RecentEvents.SequenceEqual(original.RecentEvents)).IsTrue();

        var local = original with
        {
            OpponentMode = OpponentMode.Bot, RequestedBotId = "trained-model",
            RequestedBotName = new string('М', 64), EffectiveBotId = "tuned-tracker",
            EffectiveBotName = new string('Т', 64), OpponentFallbackActive = true,
            BotFallbackReason = "Модель бота не найдена.", UdpPort = 0,
            PeerAddress = null, PingMs = null, PeerNickname = null, Phase = GamePhase.GameOver, CanRematch = true
        };
        buffer = new ArrayBufferWriter<byte>();
        BrowserWebSocketProtocol.WriteSnapshot(local, buffer);
        var (localFields, localVersion, localDecoded, localAtEnd) = ReadSnapshot(buffer.WrittenMemory);
        await Assert.That(localFields).IsEqualTo(35);
        await Assert.That(localVersion).IsEqualTo(9);
        await Assert.That(localDecoded.OpponentMode).IsEqualTo(OpponentMode.Bot);
        await Assert.That(localDecoded.RequestedBotId).IsEqualTo("trained-model");
        await Assert.That(localDecoded.RequestedBotName).IsEqualTo(new string('М', 64));
        await Assert.That(localDecoded.EffectiveBotId).IsEqualTo("tuned-tracker");
        await Assert.That(localDecoded.EffectiveBotName).IsEqualTo(new string('Т', 64));
        await Assert.That(localDecoded.OpponentFallbackActive).IsTrue();
        await Assert.That(localDecoded.BotFallbackReason).IsEqualTo("Модель бота не найдена.");
        await Assert.That(localDecoded.PeerNickname).IsNull();
        await Assert.That(localDecoded.CanRematch).IsTrue();
        await Assert.That(localAtEnd).IsTrue();
    }

    [Test]
    public async Task TryReadAxis_AcceptsOnlyOneVersionedIntegerControl()
    {
        foreach (var axis in new[] { -1, 0, 1 })
        {
            var bytes = SerializeControl(axis);
            await Assert.That(BrowserWebSocketProtocol.TryReadAxis(bytes, out var matchId, out var roundId, out var parsed)).IsTrue();
            await Assert.That(parsed).IsEqualTo(axis);
            await Assert.That(roundId).IsEqualTo(7);
        }

        byte[][] invalid =
        [
            [], [0x92, 0x08], [0x92, 0x06, 0x01], [0x92, 0x07, 0x01],
            [0x92, 0x09, 0x01], [0x92, 0x08, 0x02],
            [0x91, 0x08], [0x93, 0x08, 0x01, 0x00], [0x92, 0x08, 0xa1, 0x31],
            [0x92, 0x08, 0x01, 0x00], [0xc1], "{\"axis\":1}"u8.ToArray()
        ];
        foreach (var bytes in invalid)
            await Assert.That(BrowserWebSocketProtocol.TryReadAxis(bytes, out _, out _, out _)).IsFalse();

        // Invalid local browser frames must not escape the WebSocket receive loop.
        var random = new Random(13579);
        for (var length = 1; length <= 256; length++)
        {
            var noise = new byte[length];
            random.NextBytes(noise);
            BrowserWebSocketProtocol.TryReadAxis(noise, out _, out _, out _);
        }
    }

    [Test]
    public async Task TryReadAxis_RejectsInvalidContextRoundAndAxisWithinCurrentLayout()
    {
        const string match = "ffeeddccbbaa99887766554433221100";
        var valid = MessagePackSerializer.Serialize(new object?[] { 9, match, 7, 1 });
        await Assert.That(BrowserWebSocketProtocol.TryReadAxis(valid, out _, out _, out _)).IsTrue();
        foreach (var values in new object?[][]
        {
            [9, match, 0, 1], [9, match, -1, 1], [9, match, 1.0, 1], [9, match, "1", 1],
            [9, match, long.MaxValue, 1], [9, match, 7, 2], [9, match, 7, -2],
            [9, match, 7, 1.0], [9, match, 7, "1"], [9, match, 7, null],
            [9, null, 7, 1], [9, 1, 7, 1], [9, "", 7, 1], [9, match.ToUpperInvariant(), 7, 1],
            [9, new string('0', 32), 7, 1], [9, "ffeeddcc-bbaa-9988-7766-554433221100", 7, 1],
            [9, match, 7, 1, 0], [8, match, 7, 1]
        })
            await Assert.That(BrowserWebSocketProtocol.TryReadAxis(MessagePackSerializer.Serialize(values), out _, out _, out _)).IsFalse();
        await Assert.That(BrowserWebSocketProtocol.TryReadAxis(valid.Concat(new byte[] { 0 }).ToArray(), out _, out _, out _)).IsFalse();
    }

    private static byte[] SerializeControl(int axis)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(4);
        writer.Write(9);
        writer.Write("ffeeddccbbaa99887766554433221100");
        writer.Write(7);
        writer.Write(axis);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static (int FieldCount, int Version, PongSnapshot Snapshot, bool AtEnd) ReadSnapshot(
        ReadOnlyMemory<byte> bytes)
    {
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
        var fieldCount = reader.ReadArrayHeader();
        var version = reader.ReadInt32();
        var role = (PeerRole)reader.ReadInt32();
        var connection = (ConnectionState)reader.ReadInt32();
        var message = reader.ReadString()!;
        var udpPort = reader.ReadInt32();
        var addresses = new string[reader.ReadArrayHeader()];
        for (var index = 0; index < addresses.Length; index++) addresses[index] = reader.ReadString()!;
        var peerAddress = reader.TryReadNil() ? null : reader.ReadString();
        var leftY = reader.ReadDouble();
        var rightY = reader.ReadDouble();
        var ballX = reader.ReadDouble();
        var ballY = reader.ReadDouble();
        var ballVx = reader.ReadDouble();
        var ballVy = reader.ReadDouble();
        var leftScore = reader.ReadInt32();
        var rightScore = reader.ReadInt32();
        var phase = (GamePhase)reader.ReadInt32();
        var countdown = reader.ReadDouble();
        var tick = reader.ReadInt64();
        var roundId = reader.ReadInt32();
        var pingMs = reader.TryReadNil() ? (double?)null : reader.ReadDouble();
        var events = new GameEvent[reader.ReadArrayHeader()];
        for (var index = 0; index < events.Length; index++)
        {
            if (reader.ReadArrayHeader() != 5) throw new InvalidDataException("Invalid event shape.");
            events[index] = new GameEvent(reader.ReadString()!, (GameEventKind)reader.ReadInt32(),
                reader.ReadInt64(), reader.ReadDouble(), reader.ReadDouble());
        }
        var localNickname = reader.ReadString()!;
        var peerNickname = reader.TryReadNil() ? null : reader.ReadString();
        var opponentMode = (OpponentMode)reader.ReadInt32();
        var requestedBotId = reader.ReadString();
        var requestedBotName = reader.ReadString();
        var effectiveBotId = reader.ReadString();
        var effectiveBotName = reader.ReadString();
        var opponentFallbackActive = reader.ReadBoolean();
        var botFallbackReason = reader.ReadString();
        var localSide = reader.TryReadNil() ? (PaddleSide?)null : (PaddleSide)reader.ReadInt32();
        var matchId = reader.ReadString();
        var sourceId = reader.ReadString()!;
        var snapshotSequence = reader.ReadInt64();
        var canRematch = reader.ReadBoolean();
        var snapshot = new PongSnapshot(role, connection, message, udpPort, addresses, peerAddress,
            leftY, rightY, ballX, ballY, ballVx, ballVy, leftScore, rightScore, phase,
            countdown, tick, roundId, pingMs, events, localNickname, peerNickname,
            opponentMode, requestedBotId, requestedBotName, effectiveBotId, effectiveBotName,
            opponentFallbackActive, botFallbackReason, localSide, matchId, sourceId, snapshotSequence, canRematch);
        return (fieldCount, version, snapshot, reader.End);
    }
}
