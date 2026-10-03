namespace LanPong;

internal sealed record WirePacket
{
    public int Version { get; init; } = 1;
    public required string Type { get; init; }
    public string? SessionId { get; init; }
    public string? RequestId { get; init; }
    public long Sequence { get; init; }
    public int Axis { get; init; }
    public int Port { get; init; }
    public double LeftY { get; init; }
    public double RightY { get; init; }
    public double BallX { get; init; }
    public double BallY { get; init; }
    public double BallVx { get; init; }
    public double BallVy { get; init; }
    public int LeftScore { get; init; }
    public int RightScore { get; init; }
    public string Phase { get; init; } = "waiting";
    public double Countdown { get; init; }
    public int RoundId { get; init; }
}

public sealed record PongSnapshot(
    string Role,
    string Connection,
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
    string Phase,
    double Countdown,
    long Tick,
    int RoundId,
    double? PingMs);

public sealed record DiscoveredHost(string Address, int Port);
