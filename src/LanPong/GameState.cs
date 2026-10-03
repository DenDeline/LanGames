namespace LanPong;

internal enum GamePhase
{
    Waiting,
    Countdown,
    Playing,
    GameOver
}

/// <summary>An observable simulation state, independent of the UDP packet format.</summary>
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
}

internal static class GamePhaseWire
{
    public static string Format(GamePhase phase) => phase switch
    {
        GamePhase.Countdown => "countdown",
        GamePhase.Playing => "playing",
        GamePhase.GameOver => "gameover",
        _ => "waiting"
    };

    public static GamePhase Parse(string? phase) => phase switch
    {
        "countdown" => GamePhase.Countdown,
        "playing" => GamePhase.Playing,
        "gameover" => GamePhase.GameOver,
        _ => GamePhase.Waiting
    };
}
