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
            3, 4, GamePhase.Playing, 0.25, 123456, 7, 8.42);
        var buffer = new ArrayBufferWriter<byte>();

        BrowserWebSocketProtocol.WriteSnapshot(original, buffer);
        var (fieldCount, version, decoded, atEnd) = ReadSnapshot(buffer.WrittenMemory);

        await Assert.That(fieldCount).IsEqualTo(20);
        await Assert.That(version).IsEqualTo(1);
        await Assert.That(atEnd).IsTrue();
        await Assert.That(decoded with { LocalAddresses = original.LocalAddresses }).IsEqualTo(original);
        await Assert.That(decoded.LocalAddresses.SequenceEqual(original.LocalAddresses)).IsTrue();
    }

    [Test]
    public async Task TryReadAxis_AcceptsOnlyOneVersionedIntegerControl()
    {
        foreach (var axis in new[] { -1, 0, 1 })
        {
            var bytes = new byte[] { 0x92, 0x01, unchecked((byte)axis) };
            await Assert.That(BrowserWebSocketProtocol.TryReadAxis(bytes, out var parsed)).IsTrue();
            await Assert.That(parsed).IsEqualTo(axis);
        }

        byte[][] invalid =
        [
            [], [0x92, 0x01], [0x92, 0x02, 0x01], [0x92, 0x01, 0x02],
            [0x91, 0x01], [0x93, 0x01, 0x01, 0x00], [0x92, 0x01, 0xa1, 0x31],
            [0x92, 0x01, 0x01, 0x00], [0xc1], "{\"axis\":1}"u8.ToArray()
        ];
        foreach (var bytes in invalid)
            await Assert.That(BrowserWebSocketProtocol.TryReadAxis(bytes, out _)).IsFalse();

        // Invalid local browser frames must not escape the WebSocket receive loop.
        var random = new Random(13579);
        for (var length = 1; length <= 256; length++)
        {
            var noise = new byte[length];
            random.NextBytes(noise);
            BrowserWebSocketProtocol.TryReadAxis(noise, out _);
        }
    }

    private static (int FieldCount, int Version, PongSnapshot Snapshot, bool AtEnd) ReadSnapshot(
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
        var snapshot = new PongSnapshot(role, connection, message, udpPort, addresses, peerAddress,
            leftY, rightY, ballX, ballY, ballVx, ballVy, leftScore, rightScore, phase,
            countdown, tick, roundId, pingMs);
        return (fieldCount, version, snapshot, reader.End);
    }
}
