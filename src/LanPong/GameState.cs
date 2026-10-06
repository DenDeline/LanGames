using System.Text.Json.Serialization;
using MessagePack;

namespace LanPong;

[JsonConverter(typeof(JsonStringEnumConverter<GamePhase>))]
public enum GamePhase
{
    // These ordinals are carried by MessagePack StatePacket.Phase (UDP v5)
    // and browser WebSocket snapshots (v2). Update both versions if they change.
    [JsonStringEnumMemberName("waiting")]
    Waiting = 0,
    [JsonStringEnumMemberName("countdown")]
    Countdown = 1,
    [JsonStringEnumMemberName("playing")]
    Playing = 2,
    [JsonStringEnumMemberName("gameover")]
    GameOver = 3
}

public enum GameEventKind
{
    Serve = 1,
    Paddle = 2,
    Wall = 3,
    Goal = 4,
    MatchStart = 5
}

/// <summary>A replayable event. Its id combines tick, order within the tick, and kind.</summary>
[MessagePackObject]
public readonly record struct GameEvent(
    [property: Key(0)] string Id,
    [property: Key(1)] GameEventKind Kind,
    [property: Key(2)] long Tick,
    [property: Key(3)] double X,
    [property: Key(4)] double Y);

/// <summary>Immutable bounded history that keeps value equality for replay checkpoints.</summary>
internal sealed class GameEventHistory : IEquatable<GameEventHistory>
{
    internal const int Capacity = 12;
    internal static GameEventHistory Empty { get; } = new([]);

    private readonly GameEvent[] _items;

    private GameEventHistory(GameEvent[] items) => _items = items;

    internal static GameEventHistory FromArray(GameEvent[] items) =>
        items.Length == 0 ? Empty : new GameEventHistory((GameEvent[])items.Clone());

    internal GameEvent[] ToArray() => _items.Length == 0 ? Array.Empty<GameEvent>() : (GameEvent[])_items.Clone();

    internal GameEventHistory Append(GameEvent item)
    {
        var length = Math.Min(_items.Length + 1, Capacity);
        var next = new GameEvent[length];
        if (length > 1)
            Array.Copy(_items, _items.Length - length + 1, next, 0, length - 1);
        next[^1] = item;
        return new GameEventHistory(next);
    }

    public bool Equals(GameEventHistory? other) =>
        other is not null && _items.AsSpan().SequenceEqual(other._items);

    public override bool Equals(object? obj) => obj is GameEventHistory other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }
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
    public long LastEventTick { get; init; }
    public int EventOrdinal { get; init; }
    public GameEventHistory RecentEvents { get; init; }
}
