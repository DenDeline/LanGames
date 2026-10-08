using System.Buffers;
using System.Text.Json;
using MessagePack;

namespace LanPong.Tests;

public sealed class PeerEnumWireTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid SessionId = Guid.ParseExact("ffeeddccbbaa99887766554433221100", "N");

    [Test]
    public async Task Snapshot_EnumValuesKeepTheBrowserJsonContract()
    {
        var modeOrdinals = Enum.GetValues<OpponentMode>().Select(mode => (int)mode);
        await Assert.That(string.Join(',', modeOrdinals)).IsEqualTo("0,1,2");

        foreach (var (role, name) in new[]
                 {
                     (PeerRole.None, "none"),
                     (PeerRole.Host, "host"),
                     (PeerRole.Guest, "guest")
                 })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(Snapshot(role, ConnectionState.Idle, GamePhase.Waiting), WebJsonOptions));
            await Assert.That(json.RootElement.GetProperty("role").GetString()).IsEqualTo(name);
        }

        foreach (var (mode, name) in new[]
                 {
                     (OpponentMode.None, "none"),
                     (OpponentMode.Lan, "lan"),
                     (OpponentMode.Bot, "bot")
                 })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(
                Snapshot(PeerRole.Host, ConnectionState.Connected, GamePhase.Playing) with
                { OpponentMode = mode }, AppJsonSerializerContext.Default.PongSnapshot));
            await Assert.That(json.RootElement.GetProperty("opponentMode").GetString()).IsEqualTo(name);
            await Assert.That(json.RootElement.GetProperty("version").GetInt32()).IsEqualTo(8);
            await Assert.That(json.RootElement.TryGetProperty("requestedOpponentMode", out _)).IsFalse();
            foreach (var property in new[] { "requestedBotId", "requestedBotName", "effectiveBotId",
                         "effectiveBotName", "botFallbackReason" })
                await Assert.That(json.RootElement.GetProperty(property).ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(json.RootElement.GetProperty("opponentFallbackActive").GetBoolean())
                .IsFalse();
        }

        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(
                   Snapshot(PeerRole.Host, ConnectionState.Connected, GamePhase.Playing) with
                   {
                       OpponentMode = OpponentMode.Bot,
                       RequestedBotId = "trained-model", RequestedBotName = "Trained profile",
                       EffectiveBotId = "tuned-tracker", EffectiveBotName = "Tuned profile",
                       OpponentFallbackActive = true, BotFallbackReason = "Модель бота не найдена."
                   }, AppJsonSerializerContext.Default.PongSnapshot)))
        {
            await Assert.That(json.RootElement.GetProperty("opponentMode").GetString())
                .IsEqualTo("bot");
            await Assert.That(json.RootElement.GetProperty("requestedBotId").GetString())
                .IsEqualTo("trained-model");
            await Assert.That(json.RootElement.GetProperty("requestedBotName").GetString())
                .IsEqualTo("Trained profile");
            await Assert.That(json.RootElement.GetProperty("effectiveBotId").GetString())
                .IsEqualTo("tuned-tracker");
            await Assert.That(json.RootElement.GetProperty("effectiveBotName").GetString())
                .IsEqualTo("Tuned profile");
            await Assert.That(json.RootElement.GetProperty("botFallbackReason").GetString())
                .IsEqualTo("Модель бота не найдена.");
            await Assert.That(json.RootElement.GetProperty("opponentFallbackActive").GetBoolean())
                .IsTrue();
        }

        foreach (var (connection, name) in new[]
                 {
                     (ConnectionState.Idle, "idle"),
                     (ConnectionState.Waiting, "waiting"),
                     (ConnectionState.Connecting, "connecting"),
                     (ConnectionState.Connected, "connected"),
                     (ConnectionState.IncomingChallenge, "incomingChallenge"),
                     (ConnectionState.AwaitingAcceptance, "awaitingAcceptance"),
                     (ConnectionState.Searching, "searching")
                 })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(Snapshot(PeerRole.Host, connection, GamePhase.Waiting), WebJsonOptions));
            await Assert.That(json.RootElement.GetProperty("connection").GetString()).IsEqualTo(name);
        }

        foreach (var (phase, name) in new[]
                 {
                     (GamePhase.Waiting, "waiting"),
                     (GamePhase.Countdown, "countdown"),
                     (GamePhase.Playing, "playing"),
                     (GamePhase.GameOver, "gameover")
                 })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(Snapshot(PeerRole.Host, ConnectionState.Connected, phase), WebJsonOptions));
            await Assert.That(json.RootElement.GetProperty("phase").GetString()).IsEqualTo(name);
        }
    }

    [Test]
    public async Task StatePacket_PhaseIsNumericAndRoundTripsAsEnum()
    {
        var bytes = WirePacketCodec.Serialize(new StatePacket
        {
            SessionId = SessionId, Phase = GamePhase.Playing, Sequence = 42,
            ServeDirection = 1, RecentEvents = []
        });
        var (isInteger, phaseValue) = ReadPackedPhase(bytes);

        await Assert.That(isInteger).IsTrue();
        await Assert.That(phaseValue).IsEqualTo(2);
        await Assert.That(WirePacketCodec.TryDeserialize(bytes, out var decoded)).IsTrue();
        await Assert.That(decoded is StatePacket { Phase: GamePhase.Playing }).IsTrue();
        await Assert.That(((StatePacket)decoded!).ToGameState().Phase).IsEqualTo(GamePhase.Playing);
    }

    [Test]
    public async Task StatePacket_RejectsUndefinedPhaseValue()
    {
        var bytes = WirePacketCodec.Serialize(new StatePacket { SessionId = SessionId, Phase = (GamePhase)255 });

        await Assert.That(WirePacketCodec.TryDeserialize(bytes, out var decoded)).IsFalse();
        await Assert.That(decoded).IsNull();
    }

    [Test]
    public async Task StatePacket_RejectsLegacyStringPhasePayload()
    {
        var bytes = LegacyV2StatePacket();

        await Assert.That(WirePacketCodec.TryDeserialize(bytes, out var decoded)).IsFalse();
        await Assert.That(decoded).IsNull();
    }

    private static PongSnapshot Snapshot(PeerRole role, ConnectionState connection, GamePhase phase) => new(
        Role: role, Connection: connection, Message: "", UdpPort: 0,
        LocalAddresses: [], PeerAddress: null,
        LeftY: 0.5, RightY: 0.5, BallX: 0.5, BallY: 0.5,
        BallVx: 0, BallVy: 0, LeftScore: 0, RightScore: 0,
        Phase: phase, Countdown: 0, Tick: 0, RoundId: 0, PingMs: null, RecentEvents: [],
        LocalNickname: "Игрок", PeerNickname: null, OpponentMode: OpponentMode.None);

    private static (bool IsInteger, int Value) ReadPackedPhase(byte[] bytes)
    {
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
        if (reader.ReadArrayHeader() != 2 || reader.ReadInt32() != 5 || reader.ReadArrayHeader() <= 11)
            return (false, 0);

        for (var key = 0; key < 11; key++) reader.Skip();
        return reader.NextMessagePackType == MessagePackType.Integer
            ? (true, reader.ReadInt32())
            : (false, 0);
    }

    private static byte[] LegacyV2StatePacket()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(2);
        writer.Write(5); // StatePacket union tag.
        writer.WriteArrayHeader(14);
        writer.Write(2); // Protocol v2 used a string phase.
        writer.Write("session");
        writer.Write(42L);
        writer.Write(0.5);
        writer.Write(0.5);
        writer.Write(0.5);
        writer.Write(0.5);
        writer.Write(0.55);
        writer.Write(0.19);
        writer.Write(0);
        writer.Write(0);
        writer.Write("playing");
        writer.Write(0.0);
        writer.Write(1);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
