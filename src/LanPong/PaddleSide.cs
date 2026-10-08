using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanPong;

/// <summary>A resolved physical paddle, independent of network authority.</summary>
[JsonConverter(typeof(PaddleSideJsonConverter))]
public enum PaddleSide
{
    // Zero is deliberately invalid. Keep these explicit ordinals when adding wire contracts.
    [JsonStringEnumMemberName("left")]
    Left = 1,
    [JsonStringEnumMemberName("right")]
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

// Numeric JSON values are never physical assignments or initial preferences.
internal sealed class PaddleSideJsonConverter : JsonConverter<PaddleSide>
{
    public override PaddleSide Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "left" => PaddleSide.Left,
            "right" => PaddleSide.Right,
            _ => throw new JsonException("A resolved paddle side must be left or right.")
        } : throw new JsonException("A resolved paddle side must be a string.");

    public override void Write(Utf8JsonWriter writer, PaddleSide value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PaddleSide.Left => "left", PaddleSide.Right => "right",
            _ => throw new JsonException("A resolved paddle side must be left or right.")
        });
}

/// <summary>An initial choice; Random is resolved once when a session is admitted.</summary>
[JsonConverter(typeof(InitialSidePreferenceJsonConverter))]
public enum InitialSidePreference
{
    [JsonStringEnumMemberName("left")]
    Left = 1,
    [JsonStringEnumMemberName("right")]
    Right = 2,
    [JsonStringEnumMemberName("random")]
    Random = 3
}

internal sealed class InitialSidePreferenceJsonConverter : JsonConverter<InitialSidePreference>
{
    public override InitialSidePreference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "left" => InitialSidePreference.Left,
            "right" => InitialSidePreference.Right,
            "random" => InitialSidePreference.Random,
            _ => throw new JsonException("An initial side preference must be left, right or random.")
        } : throw new JsonException("An initial side preference must be a string.");

    public override void Write(Utf8JsonWriter writer, InitialSidePreference value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            InitialSidePreference.Left => "left", InitialSidePreference.Right => "right",
            InitialSidePreference.Random => "random",
            _ => throw new JsonException("An initial side preference must be left, right or random.")
        });
}
