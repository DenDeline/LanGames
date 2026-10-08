using System.Buffers;
using System.Text;
using MessagePack;

namespace LanPong.Tests;

public sealed class WirePacketCodecTests
{
    private static readonly Guid ChallengeId = Guid.ParseExact("00112233445566778899aabbccddeeff", "N");
    private static readonly Guid SessionId = Guid.ParseExact("ffeeddccbbaa99887766554433221100", "N");
    private static readonly Guid RestartId = Guid.ParseExact("0123456789abcdef0123456789abcdef", "N");
    private const string GuestNickname = "Guest";
    private const string HostNickname = "Хозяин";

    [Test]
    public async Task EveryPacketType_RoundTripsWithItsStableUnionTag()
    {
        var packets = CreatePackets();

        for (var index = 0; index < packets.Length; index++)
        {
            var original = packets[index];
            var bytes = WirePacketCodec.Serialize(original);
            var decoded = WirePacketCodec.TryDeserialize(bytes, out var packet);

            await Assert.That(bytes.Length < WirePacketCodec.MaxPacketBytes).IsTrue();
            await Assert.That(bytes[0]).IsEqualTo((byte)0x92); // [union tag, payload]
            await Assert.That(bytes[1]).IsEqualTo((byte)(index + 2));
            await Assert.That(decoded).IsTrue();
            await Assert.That(packet?.GetType()).IsEqualTo(original.GetType());
            await AssertPacketEquivalent(original, packet);
        }
    }

    [Test]
    public async Task HelloPacket_WritesGuidAsNativeSixteenByteBinary()
    {
        var bytes = WirePacketCodec.Serialize(new HelloPacket
        {
            RequestId = ChallengeId, Nickname = GuestNickname
        });
        var expected = Convert.FromHexString("92029309C41033221100554477668899AABBCCDDEEFFA54775657374");

        await Assert.That(bytes.SequenceEqual(expected)).IsTrue();
        await Assert.That(WirePacketCodec.TryDeserialize(expected, out var decoded)).IsTrue();
        await Assert.That(decoded is HelloPacket { RequestId: var id, Nickname: GuestNickname } &&
                          id == ChallengeId).IsTrue();
    }

    [Test]
    public async Task TryDeserialize_RejectsFormerStringGuidAndWrongNativeLength()
    {
        var stringGuid = new byte[] { 0x92, 0x02, 0x93, 0x09, 0xd9, 0x20 }
            .Concat(Encoding.ASCII.GetBytes(ChallengeId.ToString("N")))
            .Concat(new byte[] { 0xa5, (byte)'G', (byte)'u', (byte)'e', (byte)'s', (byte)'t' }).ToArray();
        var shortBinaryGuid = new byte[] { 0x92, 0x02, 0x93, 0x09, 0xc4, 0x0f }
            .Concat(ChallengeId.ToByteArray()[..15])
            .Concat(new byte[] { 0xa5, (byte)'G', (byte)'u', (byte)'e', (byte)'s', (byte)'t' }).ToArray();

        await Assert.That(WirePacketCodec.TryDeserialize(stringGuid, out _)).IsFalse();
        await Assert.That(WirePacketCodec.TryDeserialize(shortBinaryGuid, out _)).IsFalse();
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
        var oldBinary = WirePacketCodec.Serialize(new HelloPacket
        {
            Version = WirePacket.CurrentVersion - 1, RequestId = ChallengeId,
            Nickname = GuestNickname
        });
        var oldJson = "{\"version\":1,\"type\":\"hello\"}"u8.ToArray();
        var oldFlatMessagePack = new byte[] { 0x92, 0x02, 0xa5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };

        await Assert.That(WirePacketCodec.TryDeserialize(oldBinary, out _)).IsFalse();
        await Assert.That(WirePacketCodec.TryDeserialize(oldJson, out _)).IsFalse();
        await Assert.That(WirePacketCodec.TryDeserialize(oldFlatMessagePack, out _)).IsFalse();
    }

    [Test]
    public async Task TryDeserialize_RejectsRetiredDiscoveryUnionTags()
    {
        // Discovery moved to mDNS; tags 0 and 1 must not become game packets.
        var oldDiscover = new byte[] { 0x92, 0x00, 0x91, 0x05 };
        var oldOffer = new byte[] { 0x92, 0x01, 0x92, 0x05, 0xcd, 0x6d, 0xb0 };

        await Assert.That(WirePacketCodec.TryDeserialize(oldDiscover, out _)).IsFalse();
        await Assert.That(WirePacketCodec.TryDeserialize(oldOffer, out _)).IsFalse();
    }

    [Test]
    public async Task TryDeserialize_RejectsEmptyIds()
    {
        WirePacket[] invalid =
        [
            new HelloPacket { Nickname = GuestNickname },
            new WelcomePacket { SessionId = SessionId, Nickname = HostNickname },
            new WelcomePacket { RequestId = ChallengeId, Nickname = HostNickname },
            new ChallengePendingPacket { Nickname = HostNickname },
            new ChallengeDeclinedPacket(),
            new CancelChallengePacket(),
            new InputPacket { SessionId = Guid.Empty, Sequence = 1, Tick = 1, Axes = [0] },
            new StatePacket { SessionId = Guid.Empty, ServeDirection = 1, RecentEvents = [] },
            new RestartPacket { SessionId = SessionId },
            new RestartPacket { RequestId = RestartId },
            new PingPacket { SessionId = Guid.Empty },
            new PongPacket { SessionId = Guid.Empty },
            new ByePacket { SessionId = Guid.Empty }
        ];

        foreach (var packet in invalid)
            await Assert.That(WirePacketCodec.TryDeserialize(WirePacketCodec.Serialize(packet), out _)).IsFalse();
    }

    [Test]
    public async Task TryDeserialize_RejectsNullEmptyAndBlankHandshakeNicknames()
    {
        WirePacket[] invalid =
        [
            new HelloPacket { RequestId = ChallengeId, Nickname = null! },
            new HelloPacket { RequestId = ChallengeId, Nickname = "" },
            new HelloPacket { RequestId = ChallengeId, Nickname = "   " },
            new WelcomePacket { SessionId = SessionId, RequestId = ChallengeId, Nickname = null! },
            new WelcomePacket { SessionId = SessionId, RequestId = ChallengeId, Nickname = "" },
            new WelcomePacket { SessionId = SessionId, RequestId = ChallengeId, Nickname = "   " },
            new ChallengePendingPacket { RequestId = ChallengeId, Nickname = null! },
            new ChallengePendingPacket { RequestId = ChallengeId, Nickname = "" },
            new ChallengePendingPacket { RequestId = ChallengeId, Nickname = "   " }
        ];

        foreach (var packet in invalid)
            await Assert.That(WirePacketCodec.TryDeserialize(WirePacketCodec.Serialize(packet), out _)).IsFalse();
    }

    [Test]
    public async Task TryDeserialize_RejectsOversizedMalformedAndUnknownUnionTag()
    {
        var oversized = new byte[WirePacketCodec.MaxPacketBytes + 1];
        var malformed = new byte[] { 0x92, 0x05 };
        var unknownTag = new byte[] { 0x92, 0x0d, 0x90 };

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
            HostSide = PaddleSide.Right,
            SessionId = SessionId, Sequence = 42, RoundId = 1, ServeDirection = 1, RecentEvents = []
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
            SessionId = SessionId, Sequence = 10, Tick = 100,
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
            HostSide = PaddleSide.Right,
            SessionId = SessionId, Sequence = 10, RoundId = 3,
            ServeDirection = -1, Hits = 4, HostAxis = 1,
            LastEventTick = 9, EventOrdinal = 1,
            RecentEvents = [new GameEvent("9:0:2", GameEventKind.Paddle, 9, 0.05, 0.5)]
        };

        await Assert.That(WirePacketCodec.TryDeserialize(WirePacketCodec.Serialize(state), out var decoded)).IsTrue();
        var replayState = ((StatePacket)decoded!).ToGameState();
        await Assert.That(replayState.ServeDirection).IsEqualTo(-1);
        await Assert.That(replayState.Hits).IsEqualTo(4);
        await Assert.That(replayState.RecentEvents.ToArray().SequenceEqual(state.RecentEvents)).IsTrue();

        StatePacket[] invalid =
        [
            state with { ServeDirection = 0 },
            state with { ServeDirection = 2 },
            state with { Hits = -1 },
            state with { HostAxis = 2 },
            state with { HostAxis = -2 },
            state with { RecentEvents = null },
            state with { LastEventTick = 11 },
            state with { EventOrdinal = -1 },
            state with { RecentEvents = [new GameEvent("9:0:0", (GameEventKind)0, 9, 0.05, 0.5)] },
            state with { RecentEvents = [new GameEvent("9:0:2", GameEventKind.Paddle, 11, 0.05, 0.5)] },
            state with { RecentEvents = [new GameEvent("9:0:2", GameEventKind.Paddle, 9, double.NaN, 0.5)] }
        ];

        foreach (var packet in invalid)
            await Assert.That(WirePacketCodec.TryDeserialize(WirePacketCodec.Serialize(packet), out _)).IsFalse();
    }

    [Test]
    public async Task StatePacket_WithFullEventHistory_FitsUdpDatagram()
    {
        var tick = long.MaxValue;
        var state = new StatePacket
        {
            HostSide = PaddleSide.Right,
            SessionId = SessionId, Sequence = tick,
            RoundId = int.MaxValue, ServeDirection = 1,
            LastEventTick = tick, EventOrdinal = GameEventHistory.Capacity,
            RecentEvents = Enumerable.Range(0, GameEventHistory.Capacity)
                .Select(index => new GameEvent($"{tick}:{index}:3", GameEventKind.Wall,
                    tick, 0.5, 0.5)).ToArray()
        };

        var bytes = WirePacketCodec.Serialize(state);

        await Assert.That(bytes.Length).IsLessThanOrEqualTo(WirePacketCodec.MaxPacketBytes);
        await Assert.That(WirePacketCodec.TryDeserialize(bytes, out _)).IsTrue();
    }

    [Test]
    public async Task TryDeserialize_RejectsNonphysicalSidesUnassignedRoundsAndTrailingBytes()
    {
        var packets = CreatePackets();
        var welcome = (WelcomePacket)packets[1];
        var state = (StatePacket)packets[3];
        var restart = (RestartPacket)packets[4];
        foreach (var invalid in new WirePacket[]
        {
            welcome with { HostSide = 0 }, welcome with { HostSide = (PaddleSide)3 },
            welcome with { RoundId = 0 }, welcome with { RoundId = -1 },
            state with { HostSide = 0 }, state with { HostSide = (PaddleSide)3 },
            state with { RoundId = 0 }, state with { RoundId = -1 },
            restart with { ExpectedRoundId = 0 }, restart with { ExpectedRoundId = -1 }
        })
            await Assert.That(WirePacketCodec.TryDeserialize(WirePacketCodec.Serialize(invalid), out _)).IsFalse();
        foreach (var packet in new WirePacket[] { welcome, state, restart })
        {
            var bytes = WirePacketCodec.Serialize(packet);
            await Assert.That(WirePacketCodec.TryDeserialize(bytes.Concat(new byte[] { 0 }).ToArray(), out _)).IsFalse();
        }
        // Generated formatters tolerate omitted fields, but a Welcome with no resolved side/round
        // and a State with no host side remain semantically invalid for admission.
        foreach (var includeSide in new[] { false, true })
            await Assert.That(WirePacketCodec.TryDeserialize(PackedWelcomeWithoutRound(includeSide), out _)).IsFalse();
        await Assert.That(WirePacketCodec.TryDeserialize(PackedRestartWithoutRound(), out _)).IsFalse();
        var missingStateSide = WirePacketCodec.Serialize(state)[..^1];
        missingStateSide[4] = 20; // State's array16 header: twenty complete fields omit HostSide at key 20.
        await Assert.That(WirePacketCodec.TryDeserialize(missingStateSide, out _)).IsFalse();
    }

    [Test]
    public async Task TryDeserialize_PreservesGeneratedFormatterCompatibilityForOptionalMissingAndExtraFields()
    {
        // Normal generated formatter compatibility accepts an omitted optional Ping sequence as
        // zero and skips an unknown extra field. Both complete arrays still pass packet value checks.
        foreach (var fields in new[] { 2, 4 })
        {
            var bytes = PackedPing(fields, []);
            var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
            reader.Skip();
            await Assert.That(reader.End).IsTrue();
            await Assert.That(WirePacketCodec.TryDeserialize(bytes, out var decoded)).IsTrue();
            await Assert.That(decoded is PingPacket { Sequence: 0, SessionId: var session } && session == SessionId).IsTrue();
        }
    }

    [Test]
    public async Task TryDeserialize_RejectsSmallNestedTruncatedAndHugeDeclaredContainersWithoutEscaping()
    {
        var nested = Enumerable.Repeat((byte)0x91, 900).Append((byte)0).ToArray();
        var truncatedNested = Enumerable.Repeat((byte)0x91, 900).ToArray();
        foreach (var encodedSequence in new byte[][]
        {
            nested, truncatedNested,
            [0xdd, 0xff, 0xff, 0xff, 0xff], // Array declares UInt32.MaxValue elements, no elements follow.
            [0xdf, 0xff, 0xff, 0xff, 0xff], // Map declares UInt32.MaxValue pairs, no pairs follow.
            [0xc6, 0xff, 0xff, 0xff, 0xff], // Binary declares UInt32.MaxValue bytes, no body follows.
            [0xd9, 0x20, (byte)'a'], // Truncated string body.
            [0xc4], // Truncated binary header.
            [] // The otherwise current payload declares a missing sequence value.
        })
        {
            var bytes = PackedPing(3, encodedSequence);
            await Assert.That(bytes.Length).IsLessThanOrEqualTo(WirePacketCodec.MaxPacketBytes);
            await Assert.That(WirePacketCodec.TryDeserialize(bytes, out var decoded)).IsFalse();
            await Assert.That(decoded).IsNull();
        }
    }

    private static byte[] PackedWelcomeWithoutRound(bool includeSide)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(2);
        writer.Write(3);
        writer.WriteArrayHeader(includeSide ? 5 : 4);
        writer.Write(WirePacket.CurrentVersion);
        writer.Write(SessionId.ToByteArray());
        writer.Write(ChallengeId.ToByteArray());
        writer.Write(HostNickname);
        if (includeSide) writer.Write((int)PaddleSide.Right);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] PackedRestartWithoutRound()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(2);
        writer.Write(6);
        writer.WriteArrayHeader(3);
        writer.Write(WirePacket.CurrentVersion);
        writer.Write(SessionId.ToByteArray());
        writer.Write(RestartId.ToByteArray());
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] PackedPing(int fields, byte[] encodedSequence)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(2);
        writer.Write(7);
        writer.WriteArrayHeader(fields);
        writer.Write(WirePacket.CurrentVersion);
        writer.Write(SessionId.ToByteArray());
        if (fields == 4)
        {
            writer.Write(0L);
            writer.Write(0);
        }
        writer.Flush();
        return [.. buffer.WrittenSpan, .. encodedSequence];
    }

    private static WirePacket[] CreatePackets() =>
    [
        new HelloPacket { RequestId = ChallengeId, Nickname = GuestNickname },
        new WelcomePacket { SessionId = SessionId, RequestId = ChallengeId, Nickname = HostNickname, HostSide = PaddleSide.Right, RoundId = 7 },
        new InputPacket
        {
            SessionId = SessionId, Sequence = 42, Tick = 123456,
            RoundId = 7, Axes = [-1, 0, 1]
        },
        new StatePacket
        {
            HostSide = PaddleSide.Right,
            SessionId = SessionId, Sequence = 123456,
            LeftY = 0.14, RightY = 0.86,
            BallX = 0.35, BallY = 0.64, BallVx = -0.72, BallVy = 0.31,
            LeftScore = 2, RightScore = 3, Phase = GamePhase.Playing,
            Countdown = 1.25, RoundId = 7,
            ServeDirection = -1, Hits = 4, HostAxis = 1,
            LastEventTick = 123450, EventOrdinal = 1,
            RecentEvents = [new GameEvent("123450:0:3", GameEventKind.Wall, 123450, 0.4, 0.012)]
        },
        new RestartPacket { SessionId = SessionId, RequestId = RestartId, ExpectedRoundId = 7 },
        new PingPacket { SessionId = SessionId, Sequence = 43 },
        new PongPacket { SessionId = SessionId, Sequence = 43 },
        new ByePacket { SessionId = SessionId },
        new ChallengePendingPacket { RequestId = ChallengeId, Nickname = HostNickname },
        new ChallengeDeclinedPacket { RequestId = ChallengeId },
        new CancelChallengePacket { RequestId = ChallengeId }
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

        if (original is StatePacket state)
        {
            await Assert.That(decoded is StatePacket).IsTrue();
            var decodedState = (StatePacket)decoded!;
            await Assert.That(decodedState.RecentEvents!.SequenceEqual(state.RecentEvents!)).IsTrue();
            await Assert.That(decodedState with { RecentEvents = state.RecentEvents }).IsEqualTo(state);
            return;
        }

        await Assert.That(decoded).IsEqualTo(original);
    }
}
