using MessagePack;

namespace LanPong;

[Union(0, typeof(DiscoverPacket))]
[Union(1, typeof(OfferPacket))]
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
    // Version 3 encodes StatePacket.Phase as a MessagePack enum integer.
    public const int CurrentVersion = 3;

    [Key(0)]
    public int Version { get; set; } = CurrentVersion;
}

[MessagePackObject]
public sealed record DiscoverPacket : WirePacket;

[MessagePackObject]
public sealed record OfferPacket : WirePacket
{
    [Key(1)]
    public int Port { get; init; }
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
    public int Axis { get; init; }
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

    internal GameState ToGameState() => new()
    {
        LeftY = LeftY, RightY = RightY,
        BallX = BallX, BallY = BallY,
        BallVx = BallVx, BallVy = BallVy,
        LeftScore = LeftScore, RightScore = RightScore,
        Phase = Phase, Countdown = Countdown,
        TickNumber = Sequence, RoundId = RoundId
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

    internal static bool TryDeserialize(ReadOnlyMemory<byte> data, out WirePacket? packet)
    {
        packet = null;
        if (data.IsEmpty || data.Length > MaxPacketBytes) return false;

        try
        {
            var decoded = MessagePackSerializer.Deserialize<WirePacket>(data, Options);
            if (decoded is not { Version: WirePacket.CurrentVersion } ||
                decoded is StatePacket state && !Enum.IsDefined(state.Phase))
                return false;

            packet = decoded;
            return true;
        }
        catch (MessagePackSerializationException)
        {
            return false;
        }
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
    double? PingMs);

public sealed record DiscoveredHost(string Address, int Port);
