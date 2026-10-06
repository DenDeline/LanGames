using System.Text.Json.Serialization;

namespace LanPong;

/// <summary>The player's responsibility in this process.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeerRole>))]
public enum PeerRole
{
    // These values are carried by browser WebSocket MessagePack v2.
    // Changing one requires a browser protocol version change.
    [JsonStringEnumMemberName("none")]
    None = 0,
    [JsonStringEnumMemberName("host")]
    Host = 1,
    [JsonStringEnumMemberName("guest")]
    Guest = 2
}

/// <summary>The current connection lifecycle visible to the browser.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionState>))]
public enum ConnectionState
{
    // These values are carried by browser WebSocket MessagePack v2.
    [JsonStringEnumMemberName("idle")]
    Idle = 0,
    [JsonStringEnumMemberName("waiting")]
    Waiting = 1,
    [JsonStringEnumMemberName("connecting")]
    Connecting = 2,
    [JsonStringEnumMemberName("connected")]
    Connected = 3,
    [JsonStringEnumMemberName("incomingChallenge")]
    IncomingChallenge = 4,
    [JsonStringEnumMemberName("awaitingAcceptance")]
    AwaitingAcceptance = 5
}
