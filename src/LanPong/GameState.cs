using System.Text.Json.Serialization;

namespace LanPong;

[JsonConverter(typeof(JsonStringEnumConverter<GamePhase>))]
public enum GamePhase
{
    // These ordinals are carried by MessagePack StatePacket.Phase (UDP v4)
    // and browser WebSocket snapshots (v1). Update both versions if they change.
    [JsonStringEnumMemberName("waiting")]
    Waiting = 0,
    [JsonStringEnumMemberName("countdown")]
    Countdown = 1,
    [JsonStringEnumMemberName("playing")]
    Playing = 2,
    [JsonStringEnumMemberName("gameover")]
    GameOver = 3
}

/// <summary>A complete simulation state, independent of the UDP packet format.</summary>
internal readonly record struct GameState
{
    public double LeftY { get; init; }
    public double RightY { get; init; }
    public double BallX { get; init; }
    public double BallY { get; init; }
    public double BallVx { get; init; }
    public double BallVy { get; init; }
    public int LeftScore { get; init; }
    public int RightScore { get; init; }
    public GamePhase Phase { get; init; }
    public double Countdown { get; init; }
    public long TickNumber { get; init; }
    public int RoundId { get; init; }
    public int ServeDirection { get; init; }
    public int Hits { get; init; }
}
