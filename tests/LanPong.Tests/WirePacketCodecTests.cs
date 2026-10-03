using System.Text;
using LanPong;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LanPong.Tests;

public sealed class WirePacketCodecTests
{
    [Test]
    public async Task EveryPacketType_RoundTripsWithItsStableUnionTag()
    {
        WirePacket[] packets =
        [
            new DiscoverPacket(),
            new OfferPacket { Port = 28080 },
            new HelloPacket(),
            new WelcomePacket { SessionId = "session" },
            new InputPacket { SessionId = "session", Sequence = 42, Axis = -1 },
            new StatePacket
            {
                SessionId = "session", Sequence = 123456,
                LeftY = 0.14, RightY = 0.86,
                BallX = 0.35, BallY = 0.64, BallVx = -0.72, BallVy = 0.31,
                LeftScore = 2, RightScore = 3, Phase = "playing",
                Countdown = 1.25, RoundId = 7
            },
            new RestartPacket { SessionId = "session", RequestId = "restart" },
            new PingPacket { SessionId = "session", Sequence = 43 },
            new PongPacket { SessionId = "session", Sequence = 43 },
            new ByePacket { SessionId = "session" }
        ];

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
            await Assert.That(packet).IsEqualTo(original);
        }
    }

    [Test]
    public async Task TryDeserialize_RejectsPreviousProtocolAndJson()
    {
        var oldBinary = WirePacketCodec.Serialize(new OfferPacket { Version = 1, Port = 28080 });
        var oldJson = Encoding.UTF8.GetBytes("{\"version\":1,\"type\":\"offer\",\"port\":28080}");
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

        var valid = WirePacketCodec.Serialize(new StatePacket { SessionId = "test", Sequence = 42 });
        for (var index = 0; index < valid.Length; index++)
        {
            var damaged = (byte[])valid.Clone();
            damaged[index] ^= 0xff;
            WirePacketCodec.TryDeserialize(damaged, out _);
        }
    }
}
