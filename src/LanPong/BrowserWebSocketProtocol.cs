using System.Buffers;
using System.Buffers.Text;
using MessagePack;

namespace LanPong;

/// <summary>The local browser WebSocket protocol, independent of the UDP wire protocol.</summary>
internal static class BrowserWebSocketProtocol
{
    internal const int Version = 9;
    internal const int SnapshotFieldCount = 35;

    // [version, role, connection, message, udpPort, localAddresses, peerAddress,
    //  leftY, rightY, ballX, ballY, ballVx, ballVy, leftScore, rightScore,
    //  phase, countdown, tick, roundId, pingMs, recentEvents, localNickname, peerNickname,
    //  opponentMode, requestedBotId, requestedBotName, effectiveBotId, effectiveBotName,
    //  opponentFallbackActive, botFallbackReason, localSide, matchId, sourceId, snapshotSequence, canRematch]
    // recentEvents: [[id, kind, tick, x, y], ...]
    internal static void WriteSnapshot(PongSnapshot snapshot, IBufferWriter<byte> buffer)
    {
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(SnapshotFieldCount);
        writer.Write(Version);
        writer.Write((int)snapshot.Role);
        writer.Write((int)snapshot.Connection);
        writer.Write(snapshot.Message);
        writer.Write(snapshot.UdpPort);
        writer.WriteArrayHeader(snapshot.LocalAddresses.Length);
        foreach (var address in snapshot.LocalAddresses) writer.Write(address);
        if (snapshot.PeerAddress is { } peerAddress) writer.Write(peerAddress);
        else writer.WriteNil();
        writer.Write(snapshot.LeftY);
        writer.Write(snapshot.RightY);
        writer.Write(snapshot.BallX);
        writer.Write(snapshot.BallY);
        writer.Write(snapshot.BallVx);
        writer.Write(snapshot.BallVy);
        writer.Write(snapshot.LeftScore);
        writer.Write(snapshot.RightScore);
        writer.Write((int)snapshot.Phase);
        writer.Write(snapshot.Countdown);
        writer.Write(snapshot.Tick);
        writer.Write(snapshot.RoundId);
        if (snapshot.PingMs is { } pingMs) writer.Write(pingMs);
        else writer.WriteNil();
        writer.WriteArrayHeader(snapshot.RecentEvents.Length);
        foreach (var gameEvent in snapshot.RecentEvents)
        {
            writer.WriteArrayHeader(5);
            writer.Write(gameEvent.Id);
            writer.Write((int)gameEvent.Kind);
            writer.Write(gameEvent.Tick);
            writer.Write(gameEvent.X);
            writer.Write(gameEvent.Y);
        }
        writer.Write(snapshot.LocalNickname);
        if (snapshot.PeerNickname is { } peerNickname) writer.Write(peerNickname);
        else writer.WriteNil();
        writer.Write((int)snapshot.OpponentMode);
        WriteNullableString(ref writer, snapshot.RequestedBotId);
        WriteNullableString(ref writer, snapshot.RequestedBotName);
        WriteNullableString(ref writer, snapshot.EffectiveBotId);
        WriteNullableString(ref writer, snapshot.EffectiveBotName);
        writer.Write(snapshot.OpponentFallbackActive);
        WriteNullableString(ref writer, snapshot.BotFallbackReason);
        if (snapshot.LocalSide is { } localSide) writer.Write((int)localSide);
        else writer.WriteNil();
        WriteNullableString(ref writer, snapshot.MatchId);
        writer.Write(snapshot.SourceId);
        writer.Write(snapshot.SnapshotSequence);
        writer.Write(snapshot.CanRematch);
        writer.Flush();
    }

    private static void WriteNullableString(ref MessagePackWriter writer, string? value)
    {
        if (value is null) writer.WriteNil();
        else writer.Write(value);
    }

    // [version, matchId, roundId, axis], where axis is -1, 0, or 1. Each frame contains one value.
    internal static bool TryReadAxis(ReadOnlyMemory<byte> data, out Guid matchId, out int roundId, out int axis)
    {
        matchId = Guid.Empty;
        roundId = 0;
        axis = 0;
        if (data.IsEmpty) return false;

        try
        {
            var reader = new MessagePackReader(new ReadOnlySequence<byte>(data));
            if (reader.ReadArrayHeader() != 4 ||
                reader.NextMessagePackType != MessagePackType.Integer ||
                reader.ReadInt32() != Version ||
                reader.NextMessagePackType != MessagePackType.String)
                return false;

            // The caller supplies one contiguous frame. Read UTF-8 in place instead of allocating
            // a match-id string on every keyboard/touch update.
            if (!reader.TryReadStringSpan(out var match) || match.Length != 32 ||
                match.IndexOfAnyExcept("0123456789abcdef"u8) >= 0 ||
                !Utf8Parser.TryParse(match, out Guid id, out var consumed, 'N') || consumed != match.Length ||
                id == Guid.Empty || reader.NextMessagePackType != MessagePackType.Integer)
                return false;
            var round = reader.ReadInt32();
            if (round <= 0 || reader.NextMessagePackType != MessagePackType.Integer) return false;
            var value = reader.ReadInt32();
            if (value is < -1 or > 1 || !reader.End) return false;
            matchId = id;
            roundId = round;
            axis = value;
            return true;
        }
        catch (Exception error) when (error is MessagePackSerializationException or InvalidOperationException
                                        or EndOfStreamException or OverflowException)
        {
            return false;
        }
    }
}
