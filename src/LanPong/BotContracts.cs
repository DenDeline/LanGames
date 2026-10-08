using System.Text.Json.Serialization;

namespace LanPong;

[JsonConverter(typeof(JsonStringEnumConverter<BotAvailabilityState>))]
internal enum BotAvailabilityState
{
    [JsonStringEnumMemberName("ready")]
    Ready,
    [JsonStringEnumMemberName("notChecked")]
    NotChecked,
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
    [JsonStringEnumMemberName("disabled")]
    Disabled
}

// Deliberately separate from BotDefinition: discovery exposes only display metadata and safe status.
// CanPlay permits a selection attempt when this entry or its configured fallback is ready/unchecked;
// an unchecked model is verified only at selection, and can still fail then.
internal sealed record BotDescriptor(string Id, string Name, string Description, string Style,
    string Difficulty, string Category, int Order, string? Glyph, bool Enabled, string? FallbackBotId,
    BotAvailabilityState Availability, string? AvailabilityReason, bool CanPlay);

internal sealed record BotCatalogResponse(string DefaultBotId, BotDescriptor[] Bots)
{
    public int Version => BrowserWebSocketProtocol.Version;
}
