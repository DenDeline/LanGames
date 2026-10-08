namespace LanPong;

/// <summary>A resolved physical paddle, independent of network authority.</summary>
public enum PaddleSide
{
    // Zero is deliberately invalid. Keep these explicit ordinals when adding wire contracts.
    Left = 1,
    Right = 2
}

internal static class PaddleSides
{
    public static bool IsPhysical(PaddleSide side) => side is PaddleSide.Left or PaddleSide.Right;

    public static void Validate(PaddleSide side)
    {
        if (!IsPhysical(side))
            throw new ArgumentOutOfRangeException(nameof(side), side, "A resolved paddle side must be left or right.");
    }

    public static PaddleSide Opposite(PaddleSide side) => side switch
    {
        PaddleSide.Left => PaddleSide.Right,
        PaddleSide.Right => PaddleSide.Left,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "A resolved paddle side must be left or right.")
    };

    public static (int LeftAxis, int RightAxis) RouteAxes(PaddleSide localSide, int localAxis, int oppositeAxis) =>
        localSide switch
        {
            PaddleSide.Left => (localAxis, oppositeAxis),
            PaddleSide.Right => (oppositeAxis, localAxis),
            _ => throw new ArgumentOutOfRangeException(nameof(localSide), localSide,
                "A resolved paddle side must be left or right.")
        };
}
