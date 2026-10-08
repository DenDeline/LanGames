using static LanPong.GameConstants;

namespace LanPong.Bots.Inference;

/// <summary>The versioned float32 input and action schema for a right-side bot.</summary>
internal static class RightBotObservationV1
{
    public const int Version = 1;
    public const string InputName = "observation";
    public const string OutputName = "logits";
    public const int InferenceCadenceTicks = 9;
    public const int FeatureCount = 10;
    public const int Length = FeatureCount;

    // Changing the order or meaning of any entry requires a new schema version.
    public static IReadOnlyList<string> FeatureNames { get; } = Array.AsReadOnly(new[]
    {
        "left_y",
        "right_y",
        "ball_x",
        "ball_y",
        "ball_vx",
        "ball_vy",
        "left_score_normalized",
        "right_score_normalized",
        "right_contact_y",
        "right_contact_time"
    });

    public static float[] Encode(GameState state)
    {
        var features = new float[FeatureCount];
        Encode(state, features);
        return features;
    }

    public static void Encode(GameState state, Span<float> destination)
    {
        if (destination.Length < FeatureCount)
            throw new ArgumentException($"Observation requires {FeatureCount} float values.", nameof(destination));

        // (center, 2 seconds) means there is no upcoming right-paddle contact.
        var contactY = ArenaCenter;
        var contactTime = 2.0;
        if (state.BallVx > 0 && state.BallX <= RightContactX)
        {
            var time = (RightContactX - state.BallX) / state.BallVx;
            var projectedY = state.BallY + state.BallVy * time;
            if (double.IsFinite(time) && double.IsFinite(projectedY))
            {
                contactY = ReflectFromWalls(projectedY);
                contactTime = Math.Clamp(time, 0, 2);
            }
        }

        destination[0] = (float)state.LeftY;
        destination[1] = (float)state.RightY;
        destination[2] = (float)state.BallX;
        destination[3] = (float)state.BallY;
        destination[4] = (float)state.BallVx;
        destination[5] = (float)state.BallVy;
        destination[6] = (float)(state.LeftScore / (double)WinningScore);
        destination[7] = (float)(state.RightScore / (double)WinningScore);
        destination[8] = (float)contactY;
        destination[9] = (float)contactTime;

        for (var index = 0; index < FeatureCount; index++)
        {
            if (!float.IsFinite(destination[index]))
                throw new ArgumentOutOfRangeException(nameof(state), "Observation values must be finite.");
        }
    }

    // The model's class order is fixed independently of GameEngine's axis clamping.
    public static int AxisFromClass(int classIndex) => classIndex switch
    {
        0 => -1,
        1 => 0,
        2 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(classIndex))
    };

    public static int ClassFromAxis(int axis) => axis switch
    {
        -1 => 0,
        0 => 1,
        1 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    private static double ReflectFromWalls(double y)
    {
        var height = BottomContactY - TopContactY;
        var period = 2 * height;
        var offset = (y - TopContactY) % period;
        if (offset < 0) offset += period;
        return TopContactY + (offset <= height ? offset : period - offset);
    }
}
