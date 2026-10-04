using System.Buffers;
using System.Text;

namespace LanPong.Tests;

public sealed class WirePacketCodecTests
{
    [Test]
    public async Task EveryPacketType_RoundTripsWithItsStableUnionTag()
    {
        var packets = CreatePackets();

        for (var tag = 0; tag < packets.Length; tag++)
        {
            var original = packets[tag];
            var bytes = WirePacketCodec.Serialize(original);
            var decoded = WirePacketCodec.TryDeserialize(bytes, out var packet);

            await Assert.That(bytes.Length < WirePacketCodec.MaxPacketBytes).IsTrue();
            await Assert.That(bytes[0]).IsEqualTo((byte)0x92); // [union tag, payload]
            await Assert.That(bytes[1]).IsEqualTo((byte)tag);
            await Assert.That(decoded).IsTrue();
            await Assert.That(packet?.GetType()).IsEqualTo(original.GetType());
            await AssertPacketEquivalent(original, packet);
        }
    }

    [Test]
    public async Task SerializeToReusableWriter_MatchesArraySerializationForEveryPacketType()
    {
        var writer = new ArrayBufferWriter<byte>();

        foreach (var original in CreatePackets())
        {
            var expected = WirePacketCodec.Serialize(original);
            writer.Clear();
            WirePacketCodec.Serialize(original, writer);

            await Assert.That(writer.WrittenCount).IsEqualTo(expected.Length);
            await Assert.That(writer.WrittenMemory.Span.SequenceEqual(expected)).IsTrue();
            await Assert.That(WirePacketCodec.TryDeserialize(writer.WrittenMemory, out var decoded)).IsTrue();
            await AssertPacketEquivalent(original, decoded);
        }
    }

    [Test]
    public async Task TryDeserialize_RejectsPreviousProtocolAndJson()
    {
        var oldBinary = WirePacketCodec.Serialize(new OfferPacket { Version = WirePacket.CurrentVersion - 1, Port = 28080 });
        var oldJson = "{\"version\":1,\"type\":\"offer\",\"port\":28080}"u8.ToArray();
        var oldFlatMessagePack = new byte[] { 0x92, 0x02, 0xa5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };

        await Assert.That(WirePacketCodec.TryDeserialize(oldBinary, out _)).IsFalse();
        await Assert.That(WirePacketCodec.TryDeserialize(oldJson, out _)).IsFalse();
        await Assert.That(WirePacketCodec.TryDeserialize(oldFlatMessagePack, out _)).IsFalse();
    }

    [Test]
    public async Task TryDeserialize_RejectsOversizedMalformedAndUnknownUnionTag()
    {
        var oversized = new byte[WirePacketCodec.MaxPacketBytes + 1];
        var malformed = new byte[] { 0x92, 0x05 };
        var unknownTag = new byte[] { 0x92, 0x0a, 0x90 };

        await Assert.That(WirePacketCodec.TryDeserialize(oversized, out _)).IsFalse();
        await Assert.That(WirePacketCodec.TryDeserialize(malformed, out _)).IsFalse();
        await Assert.That(WirePacketCodec.TryDeserialize(unknownTag, out _)).IsFalse();

        // Unrelated UDP traffic must never escape the receive loop as a decoder error.
        var random = new Random(12345);
        for (var length = 1; length <= 256; length++)
        {
            var noise = new byte[length];
            random.NextBytes(noise);
            WirePacketCodec.TryDeserialize(noise, out _);
        }

        var valid = WirePacketCodec.Serialize(new StatePacket
        {
            SessionId = "test", Sequence = 42, ServeDirection = 1
        });
        for (var index = 0; index < valid.Length; index++)
        {
            var damaged = (byte[])valid.Clone();
            damaged[index] ^= 0xff;
            WirePacketCodec.TryDeserialize(damaged, out _);
        }
    }

    [Test]
    public async Task TryDeserialize_ValidatesTickStampedInputHistory()
    {
        var input = new InputPacket
        {
            SessionId = "session", Sequence = 10, Tick = 100,
            RoundId = 3, Axes = [1, 1, 0, -1, -1, 0, 1, 0]
        };

        await Assert.That(WirePacketCodec.TryDeserialize(WirePacketCodec.Serialize(input), out _)).IsTrue();

        InputPacket[] invalid =
        [
            input with { Sequence = -1 },
            input with { Tick = 0 },
            input with { Tick = -1 },
            input with { RoundId = -1 },
            input with { Axes = null! },
            input with { Axes = [] },
            input with { Axes = [0, 0, 0, 0, 0, 0, 0, 0, 0] },
            input with { Axes = [1, 2] },
            input with { Axes = [-2, 0] }
        ];

        foreach (var packet in invalid)
            await Assert.That(WirePacketCodec.TryDeserialize(WirePacketCodec.Serialize(packet), out _)).IsFalse();
    }

    [Test]
    public async Task TryDeserialize_ValidatesHiddenSimulationState()
    {
        var state = new StatePacket
        {
            SessionId = "session", Sequence = 10, RoundId = 3,
            ServeDirection = -1, Hits = 4, HostAxis = 1
        };

        await Assert.That(WirePacketCodec.TryDeserialize(WirePacketCodec.Serialize(state), out var decoded)).IsTrue();
        var replayState = ((StatePacket)decoded!).ToGameState();
        await Assert.That(replayState.ServeDirection).IsEqualTo(-1);
        await Assert.That(replayState.Hits).IsEqualTo(4);

        StatePacket[] invalid =
        [
            state with { ServeDirection = 0 },
            state with { ServeDirection = 2 },
            state with { Hits = -1 },
            state with { HostAxis = 2 },
            state with { HostAxis = -2 }
        ];

        foreach (var packet in invalid)
            await Assert.That(WirePacketCodec.TryDeserialize(WirePacketCodec.Serialize(packet), out _)).IsFalse();
    }

    private static WirePacket[] CreatePackets() =>
    [
        new DiscoverPacket(),
        new OfferPacket { Port = 28080 },
        new HelloPacket(),
        new WelcomePacket { SessionId = "session" },
        new InputPacket
        {
            SessionId = "session", Sequence = 42, Tick = 123456,
            RoundId = 7, Axes = [-1, 0, 1]
        },
        new StatePacket
        {
            SessionId = "session", Sequence = 123456,
            LeftY = 0.14, RightY = 0.86,
            BallX = 0.35, BallY = 0.64, BallVx = -0.72, BallVy = 0.31,
            LeftScore = 2, RightScore = 3, Phase = GamePhase.Playing,
            Countdown = 1.25, RoundId = 7,
            ServeDirection = -1, Hits = 4, HostAxis = 1
        },
        new RestartPacket { SessionId = "session", RequestId = "restart" },
        new PingPacket { SessionId = "session", Sequence = 43 },
        new PongPacket { SessionId = "session", Sequence = 43 },
        new ByePacket { SessionId = "session" }
    ];

    private static async Task AssertPacketEquivalent(WirePacket original, WirePacket? decoded)
    {
        if (original is InputPacket input)
        {
            await Assert.That(decoded is InputPacket).IsTrue();
            var decodedInput = (InputPacket)decoded!;
            await Assert.That(decodedInput.Axes).IsNotNull();
            await Assert.That(decodedInput.Axes!.SequenceEqual(input.Axes!)).IsTrue();
            // Record equality compares array identity, so reuse the source array
            // after checking its decoded contents and order above.
            await Assert.That(decodedInput with { Axes = input.Axes }).IsEqualTo(input);
            return;
        }

        await Assert.That(decoded).IsEqualTo(original);
    }
}
