using System.Text.Json.Serialization;

namespace LanPong;

/// <summary>The player's responsibility in this process.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeerRole>))]
public enum PeerRole
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("host")]
    Host,
    [JsonStringEnumMemberName("guest")]
    Guest
}

/// <summary>The current connection lifecycle visible to the browser.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionState>))]
public enum ConnectionState
{
    [JsonStringEnumMemberName("idle")]
    Idle,
    [JsonStringEnumMemberName("waiting")]
    Waiting,
    [JsonStringEnumMemberName("connecting")]
    Connecting,
    [JsonStringEnumMemberName("connected")]
    Connected
}
