using System.Buffers;
using MessagePack;

namespace LanPong;

[Union(2, typeof(HelloPacket))]
[Union(3, typeof(WelcomePacket))]
[Union(4, typeof(InputPacket))]
[Union(5, typeof(StatePacket))]
[Union(6, typeof(RestartPacket))]
[Union(7, typeof(PingPacket))]
[Union(8, typeof(PongPacket))]
[Union(9, typeof(ByePacket))]
[MessagePackObject]
public abstract record WirePacket
{
    // Version 5 includes replayable game events in authoritative state packets.
    public const int CurrentVersion = 5;

    [Key(0)]
    public int Version { get; set; } = CurrentVersion;
}

[MessagePackObject]
public sealed record HelloPacket : WirePacket;

[MessagePackObject]
public sealed record WelcomePacket : WirePacket
{
    [Key(1)]
    public string? SessionId { get; init; }
}

[MessagePackObject]
public sealed record InputPacket : WirePacket
{
    [Key(1)]
    public string? SessionId { get; init; }
    [Key(2)]
    public long Sequence { get; init; }
    [Key(3)]
    public long Tick { get; init; }
    [Key(4)]
    public int RoundId { get; init; }
    // Newest input first: Axes[0] is Tick, Axes[1] is Tick - 1, etc.
    [Key(5)]
    public int[]? Axes { get; init; }
}

[MessagePackObject]
public sealed record StatePacket : WirePacket
{
    [Key(1)]
    public string? SessionId { get; init; }
    [Key(2)]
    public long Sequence { get; init; }
    [Key(3)]
    public double LeftY { get; init; }
    [Key(4)]
    public double RightY { get; init; }
    [Key(5)]
    public double BallX { get; init; }
    [Key(6)]
    public double BallY { get; init; }
    [Key(7)]
    public double BallVx { get; init; }
    [Key(8)]
    public double BallVy { get; init; }
    [Key(9)]
    public int LeftScore { get; init; }
    [Key(10)]
    public int RightScore { get; init; }
    [Key(11)]
    public GamePhase Phase { get; init; }
    [Key(12)]
    public double Countdown { get; init; }
    [Key(13)]
    public int RoundId { get; init; }
    [Key(14)]
    public int ServeDirection { get; init; }
    [Key(15)]
    public int Hits { get; init; }
    [Key(16)]
    public int HostAxis { get; init; }
    [Key(17)]
    public GameEvent[]? RecentEvents { get; init; }
    [Key(18)]
    public long LastEventTick { get; init; }
    [Key(19)]
    public int EventOrdinal { get; init; }

    internal GameState ToGameState() => new()
    {
        LeftY = LeftY, RightY = RightY,
        BallX = BallX, BallY = BallY,
        BallVx = BallVx, BallVy = BallVy,
        LeftScore = LeftScore, RightScore = RightScore,
        Phase = Phase, Countdown = Countdown,
        TickNumber = Sequence, RoundId = RoundId,
        ServeDirection = ServeDirection, Hits = Hits,
        RecentEvents = GameEventHistory.FromArray(RecentEvents ?? []),
        LastEventTick = LastEventTick, EventOrdinal = EventOrdinal
    };
}

[MessagePackObject]
public sealed record RestartPacket : WirePacket
{
    [Key(1)]
    public string? SessionId { get; init; }
    [Key(2)]
    public string? RequestId { get; init; }
}

[MessagePackObject]
public sealed record PingPacket : WirePacket
{
    [Key(1)]
    public string? SessionId { get; init; }
    [Key(2)]
    public long Sequence { get; init; }
}

[MessagePackObject]
public sealed record PongPacket : WirePacket
{
    [Key(1)]
    public string? SessionId { get; init; }
    [Key(2)]
    public long Sequence { get; init; }
}

[MessagePackObject]
public sealed record ByePacket : WirePacket
{
    [Key(1)]
    public string? SessionId { get; init; }
}

internal static class WirePacketCodec
{
    internal const int MaxPacketBytes = 1200;

    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard
        .WithSecurity(MessagePackSecurity.UntrustedData);

    internal static byte[] Serialize(WirePacket packet) => MessagePackSerializer.Serialize(packet, Options);

    internal static void Serialize(WirePacket packet, IBufferWriter<byte> writer) =>
        MessagePackSerializer.Serialize(writer, packet, Options);

    internal static bool TryDeserialize(ReadOnlyMemory<byte> data, out WirePacket? packet)
    {
        packet = null;
        if (data.IsEmpty || data.Length > MaxPacketBytes) return false;

        try
        {
            var decoded = MessagePackSerializer.Deserialize<WirePacket>(data, Options);
            if (decoded is not { Version: WirePacket.CurrentVersion } ||
                decoded is InputPacket input &&
                (input.Sequence < 0 || input.Tick <= 0 || input.RoundId < 0 ||
                 input.Axes is not { Length: >= 1 and <= NetworkConstants.InputRedundancyTicks } ||
                 input.Axes.Any(axis => axis is < -1 or > 1)) ||
                decoded is StatePacket state &&
                (!Enum.IsDefined(state.Phase) || state.Sequence < 0 || state.RoundId < 0 ||
                 state.ServeDirection is not (-1 or 1) || state.Hits < 0 ||
                 state.HostAxis is < -1 or > 1 || state.LastEventTick < -1 ||
                 state.LastEventTick > state.Sequence || state.EventOrdinal < 0 ||
                 state.RecentEvents is not { Length: <= GameEventHistory.Capacity } ||
                 !ValidEvents(state.RecentEvents!, state.Sequence)))
                return false;

            packet = decoded;
            return true;
        }
        catch (MessagePackSerializationException)
        {
            return false;
        }
    }

    private static bool ValidEvents(GameEvent[] events, long sequence)
    {
        long previousTick = -1;
        foreach (var item in events)
        {
            if (!Enum.IsDefined(item.Kind) || item.Tick < previousTick || item.Tick > sequence ||
                item.Id is null || item.Id.Length is < 5 or > 64 ||
                !double.IsFinite(item.X) || item.X is < 0 or > 1 ||
                !double.IsFinite(item.Y) || item.Y is < 0 or > 1)
                return false;
            previousTick = item.Tick;
        }
        return true;
    }
}

public sealed record PongSnapshot(
    PeerRole Role,
    ConnectionState Connection,
    string Message,
    int UdpPort,
    string[] LocalAddresses,
    string? PeerAddress,
    double LeftY,
    double RightY,
    double BallX,
    double BallY,
    double BallVx,
    double BallVy,
    int LeftScore,
    int RightScore,
    GamePhase Phase,
    double Countdown,
    long Tick,
    int RoundId,
    double? PingMs,
    GameEvent[] RecentEvents);

public sealed record DiscoveredHost(string Address, int Port);
